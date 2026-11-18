// <copyright file="BotEventMeetingPlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Threading;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Plans where a bot waits for players after it announced an opened event (see
/// <see cref="BotEventMeeting"/>). Pure decision logic - exposed for unit tests.
/// </summary>
internal static class BotEventMeetingPlanner
{
    /// <summary>
    /// Plans the meeting point of the bot for the event type.
    /// </summary>
    /// <param name="bot">The bot which announces the event.</param>
    /// <param name="type">The type of the opened event.</param>
    /// <param name="untilUtc">When the event's entrance closes.</param>
    /// <returns>The meeting, or <c>null</c> when the bot has nowhere to wait.</returns>
    /// <remarks>
    /// Blood Castle and Devil Square are entered through an NPC (Messenger of Archangel, Charon), so
    /// the bot waits next to it - on a map it may warp to, preferring the one it is on. Chaos Castle
    /// is entered with the ticket alone and has no such place; the bot waits in the safezone of its
    /// class's home town instead, and the post names the town.
    /// </remarks>
    internal static BotEventMeeting? TryPlan(Player bot, MiniGameType type, DateTime untilUtc)
    {
        var entryNpcWindow = type switch
        {
            MiniGameType.BloodCastle => NpcWindow.BloodCastle,
            MiniGameType.DevilSquare => NpcWindow.DevilSquare,
            _ => (NpcWindow?)null,
        };

        if (entryNpcWindow is not { } window)
        {
            return type == MiniGameType.ChaosCastle
                   && bot.SelectedCharacter?.CharacterClass?.HomeMap is { } homeMap
                ? new BotEventMeeting(homeMap, null, untilUtc)
                : null;
        }

        var currentMapNumber = bot.CurrentMap?.Definition.Number;
        var entryNpc = bot.GameContext.Configuration.Maps
            .SelectMany(map => map.MonsterSpawns
                .Where(spawn => spawn.MonsterDefinition?.NpcWindow == window)
                .Select(spawn => (Map: map, Position: new Point(spawn.X1, spawn.Y1))))
            .Where(npc => npc.Map.Number == currentMapNumber || MayWarpTo(bot, npc.Map))
            .OrderByDescending(npc => npc.Map.Number == currentMapNumber)
            .FirstOrDefault();

        return entryNpc.Map is null ? null : new BotEventMeeting(entryNpc.Map, entryNpc.Position, untilUtc);
    }

    /// <summary>
    /// Finds the tile the bot stands on next to an NPC: the nearest walkable tile within the range,
    /// preferring the safezone. The NPC's own tile is usually blocked, and a route to a blocked tile
    /// does not exist.
    /// </summary>
    /// <param name="terrain">The terrain of the map.</param>
    /// <param name="npcPosition">The position of the NPC.</param>
    /// <param name="range">The maximum distance from the NPC.</param>
    /// <returns>The tile, or <c>null</c> when none is walkable.</returns>
    internal static Point? FindStandingPoint(GameMapTerrain terrain, Point npcPosition, int range)
    {
        Point? best = null;
        var bestScore = double.MaxValue;
        for (var x = npcPosition.X - range; x <= npcPosition.X + range; x++)
        {
            for (var y = npcPosition.Y - range; y <= npcPosition.Y + range; y++)
            {
                if (x is < 0 or > byte.MaxValue
                    || y is < 0 or > byte.MaxValue
                    || (x == npcPosition.X && y == npcPosition.Y)
                    || !terrain.WalkMap[x, y])
                {
                    continue;
                }

                var point = new Point((byte)x, (byte)y);

                // A tile outside the safezone only wins when the safezone offers none in range.
                var score = point.EuclideanDistanceTo(npcPosition) + (terrain.SafezoneMap[x, y] ? 0 : range * 2);
                if (score < bestScore)
                {
                    best = point;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Determines whether the bot may warp to the map according to the warp list - the same rule the
    /// bots' navigation follows (the plain level against the class-reduced warp requirement) - and the
    /// map isn't forbidden for bots.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="map">The map.</param>
    /// <returns><c>true</c>, if the bot may warp to the map.</returns>
    internal static bool MayWarpTo(Player bot, GameMapDefinition map) => GetLegalWarps(bot, map).Any();

    /// <summary>
    /// Gets the entries of the warp list into the map which the bot may use, see <see cref="MayWarpTo"/>.
    /// A map like the Lost Tower has one for each floor, each with its own level requirement.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="map">The map.</param>
    /// <returns>The warp entries.</returns>
    internal static IEnumerable<WarpInfo> GetLegalWarps(Player bot, GameMapDefinition map)
    {
        if (bot.SelectedCharacter is not { } character
            || BotFeaturePlugIn.GetConfiguration(bot.GameContext)?.IsMapForbidden(map) == true)
        {
            return [];
        }

        var level = BotChatAudience.GetLevel(bot);
        return bot.GameContext.Configuration.WarpList.Where(warp =>
            warp.Gate?.Map?.Number == map.Number
            && character.GetEffectiveMoveLevelRequirement(warp.LevelRequirement) <= level);
    }

    /// <summary>
    /// Finds out how the bot gets to a meeting place a player named, like a player would: through the
    /// warp the player named ("devias3"), or the warp from which the named coordinates are closest to
    /// walk to - the floors of the Lost Tower, for one, aren't connected by walkable ground at all.
    /// A bot which already stands on the map and can walk to the named coordinates needs no warp.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="map">The map of the meeting place.</param>
    /// <param name="warp">The warp the player named; <c>null</c> for the map alone.</param>
    /// <param name="position">The coordinates the player named; <c>null</c> for none.</param>
    /// <returns>
    /// <c>null</c>, if the bot may not or can't get there; otherwise the gate to enter the map through
    /// (<c>null</c> for a spawn gate, or none for a bot walking there on the map) and the point to wait
    /// at (<c>null</c> for the safezone).
    /// </returns>
    /// <remarks>
    /// The terrain is read from the map definition, as the map may not be loaded. A warp which the bot's
    /// level doesn't allow is out, even when it's the one the player named - like for a player.
    /// </remarks>
    internal static (ExitGate? Gate, Point? Position)? FindMeetingEntry(Player bot, GameMapDefinition map, WarpInfo? warp, Point? position)
    {
        if (BotFeaturePlugIn.GetConfiguration(bot.GameContext)?.IsMapForbidden(map) == true)
        {
            return null;
        }

        var legalWarps = GetLegalWarps(bot, map).ToList();
        if (position is { } target)
        {
            // Coordinates typed by hand may well point at a wall or a tree: the navigator waits next to
            // them, within the same range as here.
            const int waitingRange = 3;
            var terrain = new GameMapTerrain(map);
            var waitingPoint = terrain.WalkMap[target.X, target.Y]
                ? target
                : FindStandingPoint(terrain, target, waitingRange);
            if (waitingPoint is not { } point)
            {
                return null;
            }

            if (BotWarpPlanner.FindBestReachableWarp(legalWarps, terrain.WalkMap, point, null, true, CancellationToken.None) is { Gate: { } gate })
            {
                return (gate, target);
            }

            // A bot already on the map walks there without a warp - also on a map which has no entry in
            // the warp list at all.
            return bot.CurrentMap?.Definition.Number == map.Number
                   && BotWarpPlanner.CanWalk(terrain.WalkMap, bot.Position, point, CancellationToken.None)
                ? (null, target)
                : null;
        }

        if (legalWarps.Count == 0)
        {
            return null;
        }

        if (warp is not null)
        {
            return legalWarps.Contains(warp) && warp.Gate is { } namedGate
                ? (namedGate, new Point((byte)((namedGate.X1 + namedGate.X2) / 2), (byte)((namedGate.Y1 + namedGate.Y2) / 2)))
                : null;
        }

        return map.ExitGates.Any(gate => gate.IsSpawnGate) ? (null, null) : null;
    }
}
