// <copyright file="BotTownTripPlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// The decisions of an errand which needs a town NPC - the shopping trip to a merchant and the buff
/// trip to the buff NPC - and the gates such a trip uses: the safezone gate of the current map to get
/// out of the field, and the class hometown as the fallback when the current map offers the NPC nowhere.
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
    /// The steps of a town errand. An errand either starts (warp home, warp into the town of the current
    /// map, or walk off to the NPC) or continues (walk on, or trade/claim), and it can end without a trip
    /// when the bot needs nothing or no NPC anywhere offers what it needs.
    /// </summary>
    internal enum TripStep
    {
        /// <summary>
        /// The bot has nothing to get: it needs no shopping (the backpack has room, the potions are
        /// stocked) - or, for the buff, no buff NPC is configured (the Elf Soldier exists only in Season
        /// Six data), or it already has the effect, or its level is outside the buff's range.
        /// </summary>
        NoErrandNeeded,

        /// <summary>
        /// The bot has something to get, but neither this map nor its hometown has the NPC. It retries
        /// later - the NPC may be spawned, or the bot may need shopping again.
        /// </summary>
        NoNpcOnMap,

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
        /// The bot walks to the NPC, either in its town or - for an NPC which stands outside the
        /// safezone - where it stands.
        /// </summary>
        WalkToNpc,

        /// <summary>
        /// The bot stands at the NPC and can trade with it, or claim its buff.
        /// </summary>
        Claim,
    }

    /// <summary>
    /// Plans the next step of a town errand - from plain facts, so the rule is testable without a game.
    /// The caller only evaluates the facts which the trip needs: <paramref name="npcOnMap"/> only when
    /// the bot has something to get at all, and the two warp capabilities only when the NPC is missing or
    /// the bot is out in the field.
    /// </summary>
    /// <param name="needsErrand">
    /// Whether the bot has something to get which an NPC on a map offers: a merchant to sell loot and
    /// restock potions (see <see cref="BotShoppingHandler.NeedsShopping"/>), or a buff NPC it qualifies
    /// for and whose effect it does not have yet (see <see cref="BotBuffHandler.TryGetApplicableBuff"/>).
    /// </param>
    /// <param name="npcOnMap">The position of a live NPC offering the errand on the current map, or <c>null</c>.</param>
    /// <param name="inSafezone">Whether the bot currently stands in the safezone of the map.</param>
    /// <param name="canWarpToTown">
    /// Whether the current map has a spawn gate the bot can warp to, i.e. a town to warp into.
    /// </param>
    /// <param name="canWarpHome">
    /// Whether the bot has a class hometown it may go to instead (see <see cref="TryFindHomeGate"/>).
    /// </param>
    /// <returns>The step to perform.</returns>
    internal static TripStep PlanTrip(bool needsErrand, Point? npcOnMap, bool inSafezone, bool canWarpToTown, bool canWarpHome)
    {
        if (!needsErrand)
        {
            return TripStep.NoErrandNeeded;
        }

        if (npcOnMap is null)
        {
            return canWarpHome ? TripStep.WarpHome : TripStep.NoNpcOnMap;
        }

        if (!inSafezone && canWarpToTown)
        {
            return TripStep.WarpToSafezone;
        }

        return TripStep.WalkToNpc;
    }

    /// <summary>
    /// Plans the next step of a town errand which already has a target, from the distance to it.
    /// </summary>
    /// <param name="distanceToTarget">How far the bot is from the NPC it heads to.</param>
    /// <returns>The step to perform: walk on, or trade/claim when close enough.</returns>
    internal static TripStep PlanTargetStep(double distanceToTarget)
        => distanceToTarget > NpcInteractionRange ? TripStep.WalkToNpc : TripStep.Claim;

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
