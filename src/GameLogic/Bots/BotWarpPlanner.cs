// <copyright file="BotWarpPlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Judges where a bot may warp to and which gate it should take. Pure terrain and reachability logic -
/// no state of a running bot is touched, so every rule can be checked without a world.
/// </summary>
/// <remarks>
/// The verdicts are cached for the lifetime of the process where they only depend on static terrain
/// configuration, so edited terrain needs a server restart to be taken into account.
/// </remarks>
internal static class BotWarpPlanner
{
    /// <summary>Search limit for the travel pathfinder, generous enough for a full cross-map route.</summary>
    internal const int TravelSearchLimit = 20000;

    /// <summary>
    /// Cache of which maps' safezone spawn gates have a walkable tile, so a bot may safely be sent
    /// there. Keyed by the resolved safezone map's id, not its number: map numbers are not unique -
    /// the Devil Squares share number 9, and a server can host several game configurations whose
    /// maps carry the same numbers over different terrain. See <see cref="HasWalkableSpawnGate"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, bool> WalkableSpawnGateCache = new();

    /// <summary>
    /// Cache of which warp gates land on at least one walkable tile, so a bot only pays a fare for a trip
    /// it can actually take. Keyed by the gate itself - configuration reloads build new gate instances,
    /// so a reload must not reuse the old verdicts (see <see cref="HasWalkableLanding"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<ExitGate, bool> WalkableLandingCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether a bot may safely be sent to the map: the safezone spawn gate it would be recovered to
    /// has at least one walkable tile. A warp which lands a bot on a blocked tile is recovered by
    /// <see cref="Player.WarpToSafezoneAsync"/>, and for a connection-less bot that recovery runs
    /// inline (see <see cref="Offline.OfflineMapChangePlugIn"/>) - so a spawn gate with no walkable tile at
    /// all used to recurse until the stack overflowed. The player's map transitions now bound that
    /// recovery, so this filter is no longer what keeps the server alive; it keeps the bot from
    /// picking a map it cannot stand in and being bounced straight back out of it.
    /// </summary>
    /// <param name="map">The map the bot considers warping to.</param>
    /// <returns>True, if the map's resolved safezone spawn gate has a walkable tile.</returns>
    /// <remarks>
    /// The verdict is cached for the lifetime of the process, keyed by the resolved safezone map -
    /// terrain is static configuration, so it is parsed once and shared by every bot. Terrain edited
    /// through the admin panel therefore needs a server restart to be taken into account.
    /// Only maps whose spawn gate has *zero* walkable tiles are excluded. A gate with a few walkable
    /// tiles can still, under PlaceAtGateAsync's single random roll, miss and recover to town - a
    /// quality glitch, not a crash, accepted by design.
    /// </remarks>
    internal static bool HasWalkableSpawnGate(GameMapDefinition map)
    {
        // The bot is not recovered to the destination map's own gate but to its safezone map's -
        // mirroring Player.GetSpawnGateOfCurrentMapAsync. Many maps (dungeons, event maps, Icarus,
        // Karutan 2, ...) point somewhere else entirely, so checking the destination itself would
        // both reject safe maps and miss the ones which actually strand a bot.
        var safezoneMap = map.SafezoneMap ?? map;
        var mapId = safezoneMap.GetId();
        if (mapId == Guid.Empty)
        {
            // GetId falls back to Guid.Empty for anything it cannot identify, so caching under it
            // would file every such map under one entry and hand the first map's verdict to all the
            // others - a single blocked one would then reject every map there is.
            return HasWalkableSpawnGateCore(safezoneMap);
        }

        return WalkableSpawnGateCache.GetOrAdd(mapId, _ => HasWalkableSpawnGateCore(safezoneMap));
    }

    /// <summary>
    /// The uncached terrain check behind <see cref="HasWalkableSpawnGate"/>, on an already resolved
    /// safezone map.
    /// </summary>
    /// <param name="safezoneMap">The resolved safezone map.</param>
    /// <returns>True, if the map's spawn gate has a walkable tile.</returns>
    internal static bool HasWalkableSpawnGateCore(GameMapDefinition safezoneMap)
    {
        // The map is usually not loaded when a bot weighs it up, so its GameMap (which would expose
        // Terrain and SafeZoneSpawnGate ready-made) may not exist - and forcing it to load just for
        // this check would be far more expensive than parsing the terrain once.
        var terrain = new GameMapTerrain(safezoneMap);
        return terrain.GetWalkableCoordinate(safezoneMap.GetSafezoneGate(terrain)) is not null;
    }

    /// <summary>
    /// Determines whether there is a walkable route between two points of a map, not longer than the
    /// bots' travel search limit.
    /// </summary>
    /// <param name="walkMap">The walk map of the map.</param>
    /// <param name="from">The start point.</param>
    /// <param name="to">The target point.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c>, if the target can be walked to.</returns>
    internal static bool CanWalk(bool[,] walkMap, Point from, Point to, CancellationToken cancellationToken)
    {
        if (!walkMap[from.X, from.Y])
        {
            return false;
        }

        var width = walkMap.GetLength(0);
        var coordinateCount = width * walkMap.GetLength(1);
        var distancesFromTarget = ArrayPool<int>.Shared.Rent(coordinateCount);
        var pendingCoordinates = ArrayPool<int>.Shared.Rent(coordinateCount);
        try
        {
            Array.Clear(distancesFromTarget, 0, coordinateCount);
            PopulateWalkableDistancesFrom(walkMap, to, distancesFromTarget, pendingCoordinates, cancellationToken);

            // Zero means unreachable, so stored distances are offset by one.
            var encodedDistance = distancesFromTarget[(from.Y * width) + from.X];
            return encodedDistance != 0 && encodedDistance - 1 <= TravelSearchLimit;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(distancesFromTarget);
            ArrayPool<int>.Shared.Return(pendingCoordinates);
        }
    }

    /// <summary>
    /// Whether the warp's landing area holds at least one walkable tile, so the bot really arrives on the
    /// map it paid for.
    /// </summary>
    /// <param name="warp">The warp to check.</param>
    /// <returns><c>true</c>, if the bots may land there.</returns>
    /// <remarks>
    /// <see cref="Player.WarpToAsync(ExitGate)"/> places the character on a random tile of the gate and
    /// does not retry; a blocked one is recovered to the map's spawn gate - which for several maps lies on
    /// a different map entirely (Elvenland recovers to Noria, Icarus to its safezone map). A bot which
    /// paid for a trip into the field and ended up back in a starter town had taken no step at all: it
    /// stood in the very level band it tried to leave, and tried again on the next cooldown.
    /// The verdict is cached per gate for the lifetime of the process - terrain is static configuration,
    /// like <see cref="HasWalkableSpawnGate"/>, so edited terrain needs a restart.
    /// </remarks>
    internal static bool HasWalkableLanding(WarpInfo warp)
    {
        if (warp.Gate is not { Map: { } map } gate)
        {
            return false;
        }

        return WalkableLandingCache.GetOrAdd(gate, _ =>
        {
            var terrain = new GameMapTerrain(map);
            return gate.GetPossibleLandingPoints().Any(point => terrain.WalkMap[point.X, point.Y]);
        });
    }

    /// <summary>
    /// Gets the warp entry whose landing area has the shortest walkable route to a target, see
    /// <see cref="FindBestReachableWarp"/>.
    /// </summary>
    /// <param name="warps">The warp entries to choose from; the caller checks they are legal.</param>
    /// <param name="walkMap">The walk map of the target map.</param>
    /// <param name="target">The target position.</param>
    /// <param name="currentPosition">
    /// The position of the bot if it already is on the map: a gate which may land the bot there takes
    /// it nowhere; <c>null</c> otherwise.
    /// </param>
    /// <param name="ignoreBlockedLandingPoints">
    /// <c>false</c> to reject a gate as soon as any of its landing points is blocked; <c>true</c> to
    /// leave the blocked ones out - a player landing there is moved to the map's spawn gate, which is
    /// acceptable when the caller can cope with that (and the town gates of Lorencia and Devias have
    /// some blocked tiles, so they'd never be picked otherwise).
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The best reachable warp, or <c>null</c> if no gate can reach the target.</returns>
    internal static WarpInfo? FindBestReachableWarp(
        IEnumerable<WarpInfo> warps,
        bool[,] walkMap,
        Point target,
        Point? currentPosition,
        bool ignoreBlockedLandingPoints,
        CancellationToken cancellationToken)
    {
        var candidates = warps
            .Where(candidate => candidate.Gate is not null)
            .Select(candidate => (Warp: candidate, Gate: candidate.Gate!, LandingPoints: candidate.Gate!.GetPossibleLandingPoints()
                .Where(point => !ignoreBlockedLandingPoints || walkMap[point.X, point.Y])
                .ToArray()))
            .Where(candidate => candidate.LandingPoints.Length > 0
                                && (currentPosition is not { } position || !candidate.LandingPoints.Contains(position)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        // Movement on the walk map is bidirectional. One reverse flood fill from the target therefore
        // gives both reachability and shortest distance for every possible gate landing point, without
        // acquiring a full-map pathfinder once per point.
        // Cancellation from this flood fill is expected during shutdown and handled by SafeEvaluateAsync.
        cancellationToken.ThrowIfCancellationRequested();
        var width = walkMap.GetLength(0);
        var coordinateCount = width * walkMap.GetLength(1);
        var distancesFromTarget = ArrayPool<int>.Shared.Rent(coordinateCount);
        var pendingCoordinates = ArrayPool<int>.Shared.Rent(coordinateCount);
        try
        {
            Array.Clear(distancesFromTarget, 0, coordinateCount);
            PopulateWalkableDistancesFrom(
                walkMap,
                target,
                distancesFromTarget,
                pendingCoordinates,
                cancellationToken);
            WarpInfo? bestWarp = null;
            var bestWorstCaseDistance = int.MaxValue;
            foreach (var candidate in candidates)
            {
                var worstCaseDistance = GetWorstLandingDistance(candidate.LandingPoints, distancesFromTarget, width);
                if (worstCaseDistance is null
                    || worstCaseDistance > TravelSearchLimit
                    || worstCaseDistance > bestWorstCaseDistance
                    || (worstCaseDistance == bestWorstCaseDistance
                        && bestWarp?.Gate is { } bestGate
                        && GateDistance(candidate.Gate, target) >= GateDistance(bestGate, target)))
                {
                    continue;
                }

                bestWarp = candidate.Warp;
                bestWorstCaseDistance = worstCaseDistance.Value;
            }

            return bestWarp;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(distancesFromTarget);
            ArrayPool<int>.Shared.Return(pendingCoordinates);
        }
    }

    /// <summary>
    /// Gets the Manhattan distance between the center of a gate and a point.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="target">The target point.</param>
    /// <returns>The distance in tiles.</returns>
    internal static int GateDistance(Gate gate, Point target)
    {
        var centerX = (gate.X1 + gate.X2) / 2;
        var centerY = (gate.Y1 + gate.Y2) / 2;
        return Math.Abs(target.X - centerX) + Math.Abs(target.Y - centerY);
    }

    /// <summary>
    /// Determines whether a point lies inside the area of a gate.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="position">The point.</param>
    /// <returns><c>true</c>, if the point is inside the gate's area.</returns>
    internal static bool IsInside(Gate gate, Point position)
        => position.X >= gate.X1 && position.X <= gate.X2 && position.Y >= gate.Y1 && position.Y <= gate.Y2;

    /// <summary>
    /// Gets the longest distance from the target over all landing points of a gate.
    /// </summary>
    /// <param name="landingPoints">The landing points of the gate.</param>
    /// <param name="distancesFromTarget">The encoded distances of the flood fill, see <see cref="PopulateWalkableDistancesFrom"/>.</param>
    /// <param name="mapWidth">The width of the walk map.</param>
    /// <returns>The distance; <c>null</c>, if any landing point can't reach the target.</returns>
    private static int? GetWorstLandingDistance(
        IReadOnlyCollection<Point> landingPoints,
        int[] distancesFromTarget,
        int mapWidth)
    {
        var worstCaseDistance = 0;
        foreach (var landingPoint in landingPoints)
        {
            var encodedDistance = distancesFromTarget[(landingPoint.Y * mapWidth) + landingPoint.X];
            if (encodedDistance == 0)
            {
                return null;
            }

            // Zero means unreachable, so stored distances are offset by one.
            worstCaseDistance = Math.Max(worstCaseDistance, encodedDistance - 1);
        }

        return worstCaseDistance;
    }

    /// <summary>
    /// Fills the distances of all walkable tiles from a start point into the given array, offset by one
    /// so that zero means unreachable.
    /// </summary>
    /// <param name="walkMap">The walk map of the map.</param>
    /// <param name="start">The start point.</param>
    /// <param name="distances">The array which receives the encoded distances.</param>
    /// <param name="pendingCoordinates">The scratch array which holds the coordinates of the next flood fill step.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private static void PopulateWalkableDistancesFrom(
        bool[,] walkMap,
        Point start,
        int[] distances,
        int[] pendingCoordinates,
        CancellationToken cancellationToken)
    {
        if (!walkMap[start.X, start.Y])
        {
            return;
        }

        var width = walkMap.GetLength(0);
        var height = walkMap.GetLength(1);
        var readIndex = 0;
        var writeIndex = 0;
        var startIndex = (start.Y * width) + start.X;
        distances[startIndex] = 1;
        pendingCoordinates[writeIndex++] = startIndex;
        while (readIndex < writeIndex)
        {
            if ((readIndex & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var currentIndex = pendingCoordinates[readIndex++];
            var currentX = currentIndex % width;
            var currentY = currentIndex / width;
            var nextEncodedDistance = distances[currentIndex] + 1;
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    var nextX = currentX + offsetX;
                    var nextY = currentY + offsetY;
                    if ((uint)nextX >= width
                        || (uint)nextY >= height
                        || !walkMap[nextX, nextY]
                        || distances[(nextY * width) + nextX] != 0)
                    {
                        continue;
                    }

                    var nextIndex = (nextY * width) + nextX;
                    distances[nextIndex] = nextEncodedDistance;
                    pendingCoordinates[writeIndex++] = nextIndex;
                }
            }
        }
    }
}
