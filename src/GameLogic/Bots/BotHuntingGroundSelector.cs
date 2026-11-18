// <copyright file="BotHuntingGroundSelector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Rates and picks the spawn areas a bot hunts on. Pure decision and geometry logic - no state of a
/// running bot is touched, so every rule can be checked without a world.
/// </summary>
internal static class BotHuntingGroundSelector
{
    /// <summary>Width of the level band of areas we randomize between, so bots don't all stack on one spot.</summary>
    internal const int BandWidth = 3;

    /// <summary>
    /// Distance (tiles) at which a hunting ground counts as half as attractive as one the bot is standing on.
    /// See <see cref="GroundWeight"/>.
    /// </summary>
    internal const int ProximityFalloff = 64;

    /// <summary>
    /// Width (tiles) of the box a hunting ground point is sampled from when the spawn area itself is smaller.
    /// See <see cref="TryPickWalkablePoint"/>.
    /// </summary>
    internal const int GroundScatterSpan = 2 * BotMuHelperSettings.DefaultHuntingRange;

    /// <summary>
    /// Hunting grounds closer than this (tiles) to a recent death site are avoided after the same
    /// player killed the bot repeatedly (see <see cref="MUnique.OpenMU.GameLogic.Offline.OfflinePlayer.TryGetDeathSiteToAvoid"/>),
    /// so the bot farms somewhere else instead of walking back into the same lost fight.
    /// </summary>
    internal const int DeathSiteAvoidanceRange = 30;

    /// <summary>How often a point of a spawn area is picked before the pick counts as failed.</summary>
    internal const int MaxPointPickAttempts = 25;

    /// <summary>
    /// Share of the picks the best ranked map takes when the bot chooses where to hunt (see
    /// <see cref="PickByRank"/>); the runners-up split what is left, on the same terms. Below a half on
    /// purpose: at a half the fourth map of a level band and everything behind it share a few percent
    /// between them, which left Karutan, Icarus and Kanturu with three or four bots each while the top
    /// map held a hundred. The bot still prefers the best ground - every candidate it draws from is
    /// already a step up from where it stands - it just does not treat the runner-up as worthless.
    /// </summary>
    internal const double BestCandidateShare = 0.35;

    /// <summary>
    /// Rates a spawn area as a hunting ground for a bot standing at the given point.
    /// </summary>
    /// <param name="area">The spawn area.</param>
    /// <param name="from">The point the bot measures from.</param>
    /// <returns>The relative weight of the area.</returns>
    internal static int GroundWeight(MonsterSpawnArea area, Point from)
    {
        // Manhattan distances on a 256x256 map reach ~460, so a linear "300 - distance" term rated a
        // ground next to the entrance up to 299 while everything past the halfway point clamped to 1.
        // Bots enter a map through the same gate and therefore all measure from the same spot, so that
        // ratio made them queue up on the handful of spawns nearest to it. A hyperbolic falloff keeps
        // the preference for close grounds - travel is still time not spent hunting - without writing
        // off the rest of the map.
        var proximity = (ProximityFalloff * ProximityFalloff) / (ProximityFalloff + GroundDistance(area, from));
        return Math.Max(1, (int)area.Quantity) * Math.Max(1, proximity);
    }

    /// <summary>
    /// Gets the Manhattan distance between the center of a spawn area and a point.
    /// </summary>
    /// <param name="area">The spawn area.</param>
    /// <param name="from">The point.</param>
    /// <returns>The distance in tiles.</returns>
    internal static int GroundDistance(MonsterSpawnArea area, Point from)
    {
        var center = GroundCenter(area);
        return Math.Abs(from.X - center.X) + Math.Abs(from.Y - center.Y);
    }

    /// <summary>
    /// Gets the center of a spawn area.
    /// </summary>
    /// <param name="area">The spawn area.</param>
    /// <returns>The center point.</returns>
    internal static Point GroundCenter(MonsterSpawnArea area)
        => new((byte)((area.X1 + area.X2) / 2), (byte)((area.Y1 + area.Y2) / 2));

    /// <summary>
    /// Gets the level of a monster definition.
    /// </summary>
    /// <param name="definition">The monster definition.</param>
    /// <returns>The level, 0 if it has none configured.</returns>
    internal static int GetMonsterLevel(MonsterDefinition definition)
        => (int)(definition.Attributes.FirstOrDefault(a => a.AttributeDefinition == Attributes.Stats.Level)?.Value ?? 0f);

    /// <summary>
    /// Whether the spawn area holds monsters which actually stand on the map at all times. The event
    /// spawns (golden monsters, the invasion and wave areas) are configured on the regular maps too, but
    /// only exist while their event runs - a bot judging a map by them would travel to hunting grounds
    /// which are empty most of the day, and would think its current map holds far stronger monsters than
    /// it can find there. Same class of mistake as walking to a merchant that is only spawned now and
    /// then, see <see cref="BotShoppingHandler.FindMerchantPosition"/>.
    /// </summary>
    /// <param name="area">The spawn area.</param>
    /// <returns><c>true</c>, if the area spawns monsters permanently.</returns>
    internal static bool IsPermanentMonsterSpawn(MonsterSpawnArea area)
        => area is { Quantity: > 0, SpawnTrigger: SpawnTrigger.Automatic, MonsterDefinition.ObjectKind: NpcObjectKind.Monster };

    /// <summary>
    /// Picks the band of spawn areas the bot hunts in: the strongest monsters it can safely fight, or -
    /// for a mastered bot - the cheapest monsters above the master experience floor. Without a safe
    /// ground at all, the weakest monsters of the map (the bot should warp away anyway).
    /// </summary>
    /// <param name="candidates">The usable spawn areas of the map, with their monster level.</param>
    /// <param name="safe">Those of them which the bot can safely fight.</param>
    /// <param name="masterExperienceFloor">The lowest monster level which still pays a mastered bot; 0 otherwise.</param>
    /// <returns>The spawn areas of the chosen band.</returns>
    /// <remarks>
    /// A mastered bot only earns master experience above the floor (see
    /// <see cref="BotNavigator.MasterExperienceFloor"/>), so as long as this map holds such monsters, it
    /// hunts them - the weaker grounds would pay it nothing. Above the floor it then takes the WEAKEST
    /// monsters rather than the strongest: master experience hardly grows with the monster's level, so
    /// the cheapest kill above the floor is the best one.
    /// </remarks>
    internal static List<(MonsterSpawnArea Area, int Level)> SelectLevelBand(
        IReadOnlyList<(MonsterSpawnArea Area, int Level)> candidates,
        IReadOnlyList<(MonsterSpawnArea Area, int Level)> safe,
        int masterExperienceFloor)
    {
        var masterGrounds = masterExperienceFloor > 0
            ? safe.Where(x => x.Level >= masterExperienceFloor).ToList()
            : [];
        if (masterGrounds.Count > 0)
        {
            var cheapest = masterGrounds.Min(x => x.Level);
            return masterGrounds.Where(x => x.Level <= cheapest + BandWidth).ToList();
        }

        if (safe.Count > 0)
        {
            var top = safe.Max(x => x.Level);
            return safe.Where(x => x.Level >= top - BandWidth).ToList();
        }

        var bottom = candidates.Min(x => x.Level);
        return candidates.Where(x => x.Level <= bottom + BandWidth).ToList();
    }

    /// <summary>
    /// Picks an index into a list ranked best-first, each rank taking
    /// <see cref="BestCandidateShare"/> of what the ranks before it left over. Deliberately not a
    /// uniform draw - a bot should prefer the best ground it can hunt, just not to the exclusion of
    /// everything else.
    /// </summary>
    /// <param name="count">The number of ranked candidates.</param>
    /// <returns>The index of the picked candidate.</returns>
    internal static int PickByRank(int count)
    {
        for (var rank = 0; rank < count - 1; rank++)
        {
            if (Rand.NextDouble() < BestCandidateShare)
            {
                return rank;
            }
        }

        return count - 1;
    }

    /// <summary>
    /// Picks a walkable, non-safezone tile of a spawn area for the bot to walk to.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="area">The spawn area.</param>
    /// <param name="avoidCenter">A point to stay away from (see <see cref="DeathSiteAvoidanceRange"/>); <c>null</c> for none.</param>
    /// <param name="competitorSpots">The hunting spots of other bots, which are only used when the area offers nothing else.</param>
    /// <param name="mapRegions">The walkable regions of the map; <c>null</c> to accept any region.</param>
    /// <param name="region">The region of the bot, which the point has to belong to.</param>
    /// <param name="point">The picked point.</param>
    /// <returns><c>true</c>, if a point was found.</returns>
    internal static bool TryPickWalkablePoint(
        GameMap map,
        MonsterSpawnArea area,
        Point? avoidCenter,
        IReadOnlyList<HuntingSpot> competitorSpots,
        BotMapRegions? mapRegions,
        int region,
        out Point point)
    {
        int minX = Math.Min(area.X1, area.X2);
        int maxX = Math.Max(area.X1, area.X2);
        int minY = Math.Min(area.Y1, area.Y2);
        int maxY = Math.Max(area.Y1, area.Y2);

        // Not every map states its spawns as rectangles: LaCleon, for one, lists every spawn as a single
        // tile, and so do 44 of the 55 maps which hold more than ten of them. Sampling "inside" such an
        // area can only ever return that one tile, so every bot that picks it walks onto the very same
        // spot. Scatter around it instead - the bot still arrives within combat range of the monster.
        var isSingleSpot = (maxX - minX) < GroundScatterSpan && (maxY - minY) < GroundScatterSpan;
        var center = new Point((byte)((minX + maxX) / 2), (byte)((minY + maxY) / 2));

        Point? occupiedFallback = null;
        for (var attempt = 0; attempt < MaxPointPickAttempts; attempt++)
        {
            // GetRandomCoordinate already keeps to the map, retries unwalkable picks and falls back to the
            // point itself when the surroundings offer nothing - so a spawn on a ledge stays usable.
            var candidate = isSingleSpot
                ? map.Terrain.GetRandomCoordinate(center, BotMuHelperSettings.DefaultHuntingRange)
                : new Point((byte)Rand.NextInt(minX, maxX + 1), (byte)Rand.NextInt(minY, maxY + 1));
            int x = candidate.X;
            int y = candidate.Y;
            if (!map.Terrain.WalkMap[x, y]
                || map.Terrain.SafezoneMap[x, y]
                || (mapRegions is not null && mapRegions.GetRegion(candidate) != region))
            {
                continue;
            }

            // MU spawn areas can span most of the map, so a random point of an otherwise "far" area
            // may still land right at the death site to avoid - re-roll such points. If the area
            // offers nothing else, the attempts run out, and the pick fails like for any other
            // unusable area (the caller then simply retries the next tick).
            if (avoidCenter is { } avoid
                && Math.Abs(x - avoid.X) + Math.Abs(y - avoid.Y) <= DeathSiteAvoidanceRange)
            {
                continue;
            }

            var pointCandidate = new Point((byte)x, (byte)y);
            if (HuntingSpotOccupancy.IsSpotOccupied(pointCandidate, competitorSpots))
            {
                // Remember the point as a last resort: if this whole area is taken, sharing it is still
                // better than leaving the bot without a destination.
                occupiedFallback ??= pointCandidate;
                continue;
            }

            point = pointCandidate;
            return true;
        }

        if (occupiedFallback is { } fallback)
        {
            point = fallback;
            return true;
        }

        point = default;
        return false;
    }
}
