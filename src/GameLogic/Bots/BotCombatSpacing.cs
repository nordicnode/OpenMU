// <copyright file="BotCombatSpacing.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Decides how a bot uses the distance to a dangerous target: a ranged class (wizard, elf, summoner)
/// steps back instead of facetanking a melee rusher, and a Soul Master breaks the enemy's hold with
/// its teleport skill. The decisions are pure - they read the world and return a point - so the rules
/// can be checked without running a bot.
/// </summary>
internal static class BotCombatSpacing
{
    /// <summary>
    /// How close a melee opponent has to be for a ranged bot to feel pressured.
    /// </summary>
    internal const int MeleeThreatRange = 3;

    /// <summary>
    /// How far a bot steps away from a melee opponent in one retreat.
    /// </summary>
    internal const int RetreatDistance = 4;

    /// <summary>
    /// The skill number of the wizard teleport, which <see cref="PlayerActions.WizardTeleportAction"/> uses.
    /// </summary>
    internal const ushort TeleportSkillNumber = 6;

    /// <summary>
    /// The minimum time between two retreats, so a bot does not stutter back and forth every tick -
    /// a player also needs a moment to react.
    /// </summary>
    internal static readonly TimeSpan KiteCooldown = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Gets a value indicating whether the bot's class fights from a distance: wizards, elves and
    /// summoners all have spells and bows instead of a melee weapon.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <returns><c>true</c> if the class is a ranged one.</returns>
    internal static bool IsRangedClass(Player player)
        => BotBuild.GetBuildKey(player).Line is BotClassNumbers.DarkWizardNumber
            or BotClassNumbers.FairyElfNumber
            or BotClassNumbers.SummonerNumber;

    /// <summary>
    /// Gets a value indicating whether the bot can blink away with the wizard teleport right now.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <returns><c>true</c> when the character knows the teleport skill and can pay for it.</returns>
    /// <remarks>
    /// The skill list decides, not the class number: the soul master line is the usual owner, but the
    /// old configurations give the skill to the dark wizard itself. A teleport the bot cannot pay for
    /// is no escape either - it would be refused and leave the bot standing in melee reach - so the
    /// costs are part of the answer, and a bot which cannot afford it walks instead.
    /// </remarks>
    internal static bool CanTeleportAway(Player player)
        => player.SkillList?.GetSkill(TeleportSkillNumber) is { } entry
           && player.HasEnoughResources(entry);

    /// <summary>
    /// Determines whether the bot should give ground: it outranges the target, the target is a real
    /// threat (another player, or the boss of a running invasion) and it already stands within melee
    /// reach. Farming a regular monster is never a reason to kite - a bot which steps back from every
    /// wolf would just look nervous.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="target">The current combat target.</param>
    /// <param name="attackRange">The range of the attack the bot is about to use.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> if the bot should step back.</returns>
    internal static bool ShouldRetreat(OfflinePlayer bot, IAttackable target, byte attackRange, DateTime now)
    {
        if (bot.Account?.IsBot != true
            || !IsRangedClass(bot)
            || bot.IsWalking
            || bot.IsTeleporting
            || bot.CurrentMap is not { } map
            || attackRange <= MeleeThreatRange
            || !target.IsInRange(bot.Position, MeleeThreatRange)
            || bot.IsAtSafezone())
        {
            return false;
        }

        if (target is Player)
        {
            // Any player which is legally attackable is a PvP opponent - also inside Chaos Castle.
            return true;
        }

        // A boss attempt is the other fight which is worth kiting: standing in a champion's melee
        // reach costs more health than the few steps out of it.
        return target is Monster
               && bot.BossAttempt is { } attempt
               && attempt.UntilUtc > now
               && ReferenceEquals(attempt.Map, map.Definition);
    }

    /// <summary>
    /// Finds a walkable tile away from the threat, up to the given distance. It first tries straight
    /// away from the threat and then the two perpendicular escapes, because a wall or a river often
    /// blocks exactly one direction. A point only counts when the whole way to it is walkable: a free
    /// tile behind a wall is no escape for a walking bot, because the way around the obstacle would
    /// lead it right back past the melee opponent which already stands on top of it. A teleport does
    /// not need that way, so it passes <paramref name="requiresWalkablePath"/> as <c>false</c> and can
    /// blink over the obstacle.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="from">The bot's position.</param>
    /// <param name="threat">The position of the threat.</param>
    /// <param name="distance">How far to step away at most.</param>
    /// <param name="requiresWalkablePath">Whether every tile on the way has to be walkable, too.</param>
    /// <returns>The retreat point, or <c>null</c> if no walkable tile is free.</returns>
    internal static Point? FindRetreatPoint(GameMap map, Point from, Point threat, int distance, bool requiresWalkablePath = true)
    {
        var dx = from.X - threat.X;
        var dy = from.Y - threat.Y;
        if (dx == 0 && dy == 0)
        {
            // Standing on top of each other: any direction is away, pick one.
            dx = 1;
        }

        var length = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var ux = (double)dx / length;
        var uy = (double)dy / length;
        var candidates = new (double X, double Y)[]
        {
            (ux, uy),

            // The two escapes are the direct direction rotated by 45 degrees, so they point between
            // "straight away" and "sideways" - and they are sqrt(2) times as long. Scaling each by its
            // larger component keeps every candidate `step` tiles away, instead of up to twice that for
            // a diagonal threat ((1, 1) becomes (0, 2)).
            ScaleToUnit(ux - uy, uy + ux),
            ScaleToUnit(ux + uy, uy - ux),
        };

        for (var step = distance; step >= 1; step--)
        {
            foreach (var (directionX, directionY) in candidates)
            {
                var x = from.X + (int)Math.Round(directionX * step);
                var y = from.Y + (int)Math.Round(directionY * step);
                if (x is < 0 or > 255 || y is < 0 or > 255)
                {
                    continue;
                }

                var point = new Point((byte)x, (byte)y);
                if (map.Terrain.WalkMap[x, y]
                    && !map.Terrain.SafezoneMap[x, y]
                    && (!requiresWalkablePath || IsWayClear(map, from, point)))
                {
                    return point;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Rates a player target inside a free-for-all event (Chaos Castle): the lower the value, the
    /// better the target. A depleted shield is worth more than a low health bar, because the 90%
    /// shield absorption is what keeps a target alive - breaking it first is how duels are won.
    /// </summary>
    /// <param name="target">The player target.</param>
    /// <returns>The priority; lower is better.</returns>
    internal static int GetEventTargetPriority(Player target)
    {
        if (target.Attributes is not { } attributes)
        {
            return int.MaxValue;
        }

        var shieldShare = GetShare(attributes[Stats.CurrentShield], attributes[Stats.MaximumShield]);
        var healthShare = GetShare(attributes[Stats.CurrentHealth], attributes[Stats.MaximumHealth]);
        return (int)(shieldShare * 1000) + (int)(healthShare * 100);
    }

    /// <summary>
    /// Checks that every tile between the bot and a retreat point is walkable. The straight line is
    /// enough as a test: it is cheap, and a lane blocked on its direct way is exactly the case which
    /// makes the path finder lead the bot around the obstacle - and back towards the threat.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="from">The bot's position.</param>
    /// <param name="to">The retreat point.</param>
    /// <returns><c>true</c> if no tile on the way is blocked.</returns>
    private static bool IsWayClear(GameMap map, Point from, Point to)
    {
        var steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        for (var i = 1; i < steps; i++)
        {
            var x = from.X + (int)Math.Round((to.X - from.X) * (double)i / steps);
            var y = from.Y + (int)Math.Round((to.Y - from.Y) * (double)i / steps);
            if (!map.Terrain.WalkMap[x, y])
            {
                return false;
            }
        }

        return true;
    }

    private static float GetShare(float current, float maximum)
        => maximum > 0 ? Math.Clamp(current / maximum, 0f, 1f) : 0f;

    private static (double X, double Y) ScaleToUnit(double x, double y)
    {
        var length = Math.Max(Math.Abs(x), Math.Abs(y));
        return length > 0 ? (x / length, y / length) : (0, 0);
    }
}
