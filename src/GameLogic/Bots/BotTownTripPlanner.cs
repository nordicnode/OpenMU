// <copyright file="BotTownTripPlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// The decisions of a trip which needs a town NPC - so far the buff trip to the buff NPC - and the
/// gates such a trip uses: the safezone gate of the current map to get out of the field, and the
/// class hometown as the fallback when the current map offers the NPC nowhere.
/// </summary>
/// <remarks>
/// Pure decision logic, exposed for unit tests. The navigator only performs the step which is planned
/// here: it travels, warps and claims the buff. Keeping the rules free of player state means they can
/// be read and tested without a running game, and a change to them can't accidentally change the
/// traveling itself.
/// </remarks>
internal static class BotTownTripPlanner
{
    /// <summary>
    /// How close (tiles) a bot must stand to an NPC to use its dialog: close enough that walking up to
    /// it looks like a player clicking it. Used for every town NPC the bots talk to, not just the buff.
    /// </summary>
    internal const int NpcInteractionRange = 3;

    /// <summary>
    /// The steps of a buff trip. A trip either starts (warp home, warp into the town of the current map,
    /// or walk off to the NPC) or continues (walk on, or claim), and it can end without a trip when the
    /// bot needs no buff or no NPC anywhere offers one.
    /// </summary>
    internal enum BuffTripStep
    {
        /// <summary>
        /// The bot has no buff to claim: no buff NPC is configured (the Elf Soldier exists only in Season
        /// Six data), or it already has the effect, or its level is outside the buff's range.
        /// </summary>
        NoBuffNeeded,

        /// <summary>
        /// The bot needs a buff, but neither this map nor its hometown has the NPC. It retries later -
        /// the NPC may be spawned, or it may level into the buff's range.
        /// </summary>
        NoBuffNpc,

        /// <summary>
        /// The bot warps to its class hometown, whose town offers the NPC - the equivalent of a town scroll.
        /// </summary>
        WarpHome,

        /// <summary>
        /// The bot stands in the field and the map offers the NPC in its town, so it warps into the
        /// safezone first, like a player using a town portal scroll.
        /// </summary>
        WarpToSafezone,

        /// <summary>
        /// The bot walks to the buff NPC, either in its town or - for an NPC which stands outside the
        /// safezone - where it stands.
        /// </summary>
        WalkToNpc,

        /// <summary>
        /// The bot stands at the buff NPC and can claim the buff.
        /// </summary>
        Claim,
    }

    /// <summary>
    /// Plans the next step of a buff trip - from plain facts, so the rule is testable without a game.
    /// The caller only evaluates the facts which the trip needs: <paramref name="npcOnMap"/> only when
    /// the bot needs a buff at all, and the two warp capabilities only when the NPC is missing or the
    /// bot is out in the field.
    /// </summary>
    /// <param name="needsBuff">
    /// Whether a buff NPC is configured which the bot qualifies for and does not have yet (see
    /// <see cref="BotBuffHandler.TryGetApplicableBuff"/>).
    /// </param>
    /// <param name="npcOnMap">The position of a live buff NPC on the current map, or <c>null</c>.</param>
    /// <param name="inSafezone">Whether the bot currently stands in the safezone of the map.</param>
    /// <param name="canWarpToTown">
    /// Whether the current map has a spawn gate the bot can warp to, i.e. a town to warp into.
    /// </param>
    /// <param name="canWarpHome">
    /// Whether the bot has a class hometown it may go to instead (see <see cref="TryFindHomeGate"/>).
    /// </param>
    /// <returns>The step to perform.</returns>
    internal static BuffTripStep PlanBuffTrip(bool needsBuff, Point? npcOnMap, bool inSafezone, bool canWarpToTown, bool canWarpHome)
    {
        if (!needsBuff)
        {
            return BuffTripStep.NoBuffNeeded;
        }

        if (npcOnMap is null)
        {
            return canWarpHome ? BuffTripStep.WarpHome : BuffTripStep.NoBuffNpc;
        }

        if (!inSafezone && canWarpToTown)
        {
            return BuffTripStep.WarpToSafezone;
        }

        return BuffTripStep.WalkToNpc;
    }

    /// <summary>
    /// Plans the next step of a buff trip which already has a target, from the distance to it.
    /// </summary>
    /// <param name="distanceToTarget">How far the bot is from the buff NPC it heads to.</param>
    /// <returns>The step to perform: walk on, or claim the buff when close enough.</returns>
    internal static BuffTripStep PlanTargetStep(double distanceToTarget)
        => distanceToTarget > NpcInteractionRange ? BuffTripStep.WalkToNpc : BuffTripStep.Claim;

    /// <summary>
    /// Gets the safezone gate of a map the bot warps to when it wants to reach the town of the map it
    /// stands on. One of the spawn gates is picked by chance, so bots entering the same map don't all
    /// land on the same tile.
    /// </summary>
    /// <param name="mapDefinition">The map definition.</param>
    /// <returns>The spawn gate, or <c>null</c> if the map has none.</returns>
    internal static ExitGate? FindTownGate(GameMapDefinition mapDefinition)
        => mapDefinition.ExitGates?.Where(gate => gate.IsSpawnGate).SelectRandom();

    /// <summary>
    /// Finds the safezone gate of the bot's class hometown, for when an errand needs a town the current
    /// map does not offer. It is refused when the bot already stands there, when the map is forbidden by
    /// the bot settings, when the map has no spawn gate, or when going home would only lead to trivial
    /// monsters the bot has long outgrown.
    /// </summary>
    /// <param name="homeMap">The class hometown, or <c>null</c> when the class has none.</param>
    /// <param name="currentMapNumber">The number of the map the bot stands on, or <c>null</c>.</param>
    /// <param name="isForbidden">Whether the bot settings forbid the hometown map.</param>
    /// <param name="homeMonsterLevel">
    /// The level of the strongest monster of the hometown which the bot can safely fight (see
    /// <see cref="MapFit.IsTrivial"/>); 0 if it has none.
    /// </param>
    /// <param name="botLevel">The bot's reset-aware effective level.</param>
    /// <param name="allowTrivialHome">
    /// Whether going home is acceptable even though the bot would only find trivial monsters there. That
    /// is the case for an errand which needs a town (shopping, the buff NPC, leaving the market), but not
    /// for escaping a bad hunting ground: warping a level-250 bot back to Lorencia is the very thing it
    /// needs to escape from.
    /// </param>
    /// <returns>The gate, map and monster level of the hometown, or <c>null</c> if it is no destination.</returns>
    internal static (ExitGate Gate, GameMapDefinition Map, int MonsterLevel)? TryFindHomeGate(
        GameMapDefinition? homeMap,
        int? currentMapNumber,
        bool isForbidden,
        int homeMonsterLevel,
        int botLevel,
        bool allowTrivialHome)
    {
        if (homeMap is null
            || homeMap.Number == currentMapNumber
            || isForbidden
            || homeMap.GetSafezoneGate() is not { } safezoneGate)
        {
            return null;
        }

        if (!allowTrivialHome && MapFit.IsTrivial(homeMonsterLevel, botLevel))
        {
            return null;
        }

        return (safezoneGate, homeMap, homeMonsterLevel);
    }
}
