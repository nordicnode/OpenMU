// <copyright file="BotMapChoicePlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Chooses the map a bot moves to: which monster level a destination has to offer, which warp entries
/// are worth entering, and which of the gathered candidates wins the pick.
/// </summary>
/// <remarks>
/// Pure rules over monster levels, like <see cref="MapFit"/>: the candidates themselves are gathered by
/// the navigator - it is the only place which knows what the bot can safely fight, which entries it may
/// legally use and which it can afford - but what makes a candidate good enough, and which one of them is
/// taken, is decided here, so the map choice can be reasoned about and tested without a running world.
/// </remarks>
internal static class BotMapChoicePlanner
{
    /// <summary>
    /// A bot only warps to another map if it offers safe monsters at least this many levels stronger.
    /// Sized as a hysteresis band: the safety verdict for a borderline monster flips when the bot's
    /// defense changes with its buffs, and a small margin let two maps leapfrog each other on every
    /// re-evaluation - the bot ping-ponged between them on warp cooldown.
    /// </summary>
    internal const int ImprovementMargin = 8;

    /// <summary>
    /// Why a bot considers moving to another map.
    /// </summary>
    internal enum MapMoveReason
    {
        /// <summary>
        /// Nothing on the current map the bot can safely fight: aggressive monsters kill it while it
        /// cannot fight back.
        /// </summary>
        NothingSafeHere,

        /// <summary>
        /// The bot could not legally be on its current map with its plain level - a real character of that
        /// level cannot be there (e.g. a veteran stranded on a high map after its reset).
        /// </summary>
        MayNotStayLegally,

        /// <summary>
        /// The monsters on the current map are trivial for the bot: too weak to be worth the hunt.
        /// </summary>
        GroundTooWeak,

        /// <summary>
        /// The map has been paying the bot nothing for minutes, e.g. because stronger bots empty the
        /// grounds before it arrives.
        /// </summary>
        GroundBarren,
    }

    /// <summary>
    /// Gets the monster level a destination has to offer to be worth the move.
    /// </summary>
    /// <param name="escape">
    /// Whether the bot has to leave its current map regardless of its strength. Then any ground above the
    /// floor counts - a bot which cannot stay must not be picky.
    /// </param>
    /// <param name="escapeFloor">
    /// The lowest monster level an escape target has to offer (see <see cref="MapFit.EscapeTargetFloor"/>).
    /// </param>
    /// <param name="currentGroundLevel">
    /// The level of the strongest monster the bot can safely fight where it stands; 0 if it has none.
    /// </param>
    /// <returns>The level a candidate has to reach.</returns>
    internal static int TargetLevel(bool escape, int escapeFloor, int currentGroundLevel)
        => escape ? Math.Max(1, escapeFloor) : currentGroundLevel + ImprovementMargin;

    /// <summary>
    /// Gets the monster level from which a map needs to be judged at all: monsters below both the target
    /// level and the bot's own minimum can't make a map a candidate, so they need not be weighed.
    /// </summary>
    /// <param name="minimumMonsterLevel">The lowest monster level worth counting at all.</param>
    /// <param name="targetLevel">The level a candidate has to reach (see <see cref="TargetLevel"/>).</param>
    /// <returns>The level to judge the monsters of a map from.</returns>
    internal static int JudgeFrom(int minimumMonsterLevel, int targetLevel)
        => Math.Max(minimumMonsterLevel, targetLevel);

    /// <summary>
    /// Determines whether a warp entry is worth entering: its part of the map has to reach the target
    /// level, the bot has to be able to pay the fare, and the landing area has to be walkable, so the bot
    /// does not get stuck at a gate it cannot leave.
    /// </summary>
    /// <param name="entryLevel">The level of the strongest safe monster at the entry's part of the map.</param>
    /// <param name="targetLevel">The level a candidate has to reach (see <see cref="TargetLevel"/>).</param>
    /// <param name="affordable">Whether the bot can pay the warp's fare.</param>
    /// <param name="hasWalkableLanding">Whether the entry's landing area has walkable tiles.</param>
    /// <returns><c>true</c>, if the entry is worth entering.</returns>
    internal static bool IsWorthEntering(int entryLevel, int targetLevel, bool affordable, bool hasWalkableLanding)
        => entryLevel >= targetLevel && affordable && hasWalkableLanding;

    /// <summary>
    /// Determines whether ground is a step down from where the bot stands, but still the best such step
    /// found so far. Used when the current map pays nothing (see the barren map handling of the
    /// navigator): the bot steps down to the strongest ground below its own to earn its way back up,
    /// instead of retreating to the starter maps it has long outgrown.
    /// </summary>
    /// <param name="groundLevel">The level of the strongest safe monster of the candidate ground.</param>
    /// <param name="currentGroundLevel">The level of the ground the bot stands on; 0 if it can fight nothing there.</param>
    /// <param name="bestSoFar">The level of the best step down found so far; 0 for none.</param>
    /// <returns><c>true</c>, if the ground is a better step down than the one found so far.</returns>
    internal static bool IsBetterStepDown(int groundLevel, int currentGroundLevel, int bestSoFar)
    {
        // With nothing safe where it stands there is no ground above, so any huntable one is a step down.
        var from = currentGroundLevel <= 0 ? int.MaxValue : currentGroundLevel;
        return groundLevel > 0 && groundLevel < from && groundLevel > bestSoFar;
    }

    /// <summary>
    /// Picks the candidate the bot moves to: the strongest one most of the time, and one of the
    /// runners-up the rest of the time (see <see cref="BotHuntingGroundSelector.PickByRank"/>).
    /// </summary>
    /// <param name="candidates">The candidates, unordered.</param>
    /// <param name="randomizer">The bot's session random source, so its route can be replayed; <c>null</c> for the shared one.</param>
    /// <returns>The picked candidate, or <c>null</c> if there is none.</returns>
    /// <remarks>
    /// Always taking the single strongest map emptied the world: the maps of a level band differ by a few
    /// monster levels, so one of them is every bot's answer and the rest stand deserted - hundreds of bots
    /// on Vulcanus while Tarkan, Karutan and Kanturu, which their level opens, see nobody. The strongest
    /// map still wins most of the picks; the runners-up share the rest, so the population spreads over the
    /// maps a player of that band would choose between.
    /// </remarks>
    internal static MapChoice? PickStrongest(IReadOnlyList<MapChoice> candidates, IRandomizer? randomizer = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var ranked = candidates.OrderByDescending(candidate => candidate.Level).ToList();
        return ranked[BotHuntingGroundSelector.PickByRank(ranked.Count, randomizer)];
    }

    /// <summary>
    /// Plans whether a bot should look for another map at all, and under which conditions: whether it has
    /// to leave regardless of strength, whether easier ground is acceptable, and which monster level a
    /// destination has to offer.
    /// </summary>
    /// <param name="facts">What the bot knows about its current situation.</param>
    /// <returns>The plan, or <c>null</c> if the bot stays where it is.</returns>
    /// <remarks>
    /// Three situations force a bot OFF its map, bypassing the usual minimum warp level: a map without a
    /// single safe monster is hostile territory (aggressive monsters kill the bot while it cannot fight
    /// back), a map the bot could not legally warp to with its plain level is off-limits regardless of its
    /// strength - a real character of that level cannot be there, so neither may the bot (e.g. a veteran
    /// stranded on a high map after its reset) - and a map whose monsters are all trivial for the bot is no
    /// hunting ground either. The last case is what kept high-level bots on Lorencia and Noria: they found
    /// level-1 monsters there, so the nearby-target logic never left them enough quiet time to warp away
    /// (issue #1542).
    /// </remarks>
    internal static MapMovePlan? PlanMove(MapMoveFacts facts)
    {
        var hostile = facts.HasCurrentMap && facts.CurrentGroundLevel == 0;
        var trivial = MapFit.IsTrivial(facts.CurrentGroundLevel, facts.BotLevel);
        var illegal = facts.HasCurrentMap && !facts.HasLegalWarpHere && !facts.IsHomeMap;
        var mustEscape = hostile || trivial || illegal;

        // A trivial map which offers nothing better within reach is not a reason to travel on, it is a
        // reason to settle and farm what it has: the level-ups, drops and Zen from it are what opens the
        // next map. Re-checked periodically (and after every level-up, since the review is keyed on the
        // bot's level), not on every tick - scanning every map's monsters hundreds of times a second is
        // exactly the load this population cannot afford.
        if (trivial && !hostile && !illegal && facts.EscapeReviewDeferred)
        {
            return null;
        }

        if (!facts.WarpCooldownElapsed)
        {
            return null;
        }

        // Below the lowest warp requirement a bot only moves when it has to, or when the map pays it
        // nothing (see the barren map handling of the navigator).
        if (facts.PlainLevel < facts.MinWarpLevel && !mustEscape && !facts.Barren)
        {
            return null;
        }

        var reason = hostile ? MapMoveReason.NothingSafeHere
            : illegal ? MapMoveReason.MayNotStayLegally
            : trivial ? MapMoveReason.GroundTooWeak
            : MapMoveReason.GroundBarren;
        return new MapMovePlan(
            Escape: mustEscape,
            StepDown: facts.Barren,
            AllowTrivialHome: hostile,
            Trivial: trivial,
            EscapeFloor: MapFit.EscapeTargetFloor(facts.CurrentGroundLevel, hostile || illegal),
            Reason: reason);
    }

    /// <summary>
    /// Gets the reason of a map move as it appears in the bot log.
    /// </summary>
    /// <param name="reason">The reason of the move.</param>
    /// <returns>The text for the log.</returns>
    internal static string Describe(MapMoveReason reason) => reason switch
    {
        MapMoveReason.NothingSafeHere => "nothing safe here",
        MapMoveReason.MayNotStayLegally => "may not legally be here",
        MapMoveReason.GroundTooWeak => "ground too weak",
        _ => "barren",
    };

    /// <summary>
    /// A destination: the warp entry a bot enters it through, the map, and the level of the strongest
    /// monster there which the bot can safely fight.
    /// </summary>
    /// <param name="Warp">The warp entry to enter the map through.</param>
    /// <param name="Map">The map.</param>
    /// <param name="Level">The level of the strongest safe monster at the entry's part of the map.</param>
    internal readonly record struct MapChoice(WarpInfo Warp, GameMapDefinition Map, int Level);

    /// <summary>
    /// What a bot knows about its current situation when the map choice is planned.
    /// </summary>
    internal readonly record struct MapMoveFacts
    {
        /// <summary>Gets the bot's reset-aware effective level (see <see cref="BotResetHandler.GetEffectiveLevel"/>).</summary>
        internal int BotLevel { get; init; }

        /// <summary>Gets the bot's plain character level, which decides whether it may warp at all.</summary>
        internal int PlainLevel { get; init; }

        /// <summary>Gets the lowest level at which this bot warps on its own (see the navigator's warp floor).</summary>
        internal int MinWarpLevel { get; init; }

        /// <summary>Gets the level of the strongest monster the bot can safely fight on its map; 0 if it has none there.</summary>
        internal int CurrentGroundLevel { get; init; }

        /// <summary>Gets a value indicating whether the bot stands on a map at all.</summary>
        internal bool HasCurrentMap { get; init; }

        /// <summary>Gets a value indicating whether the map the bot stands on has a legal warp entry, i.e. whether a player of its plain level could be there.</summary>
        internal bool HasLegalWarpHere { get; init; }

        /// <summary>Gets a value indicating whether the map the bot stands on is its class hometown, where it may always be.</summary>
        internal bool IsHomeMap { get; init; }

        /// <summary>Gets a value indicating whether the map choice of a bot on trivial ground is deferred (see the navigator's escape review).</summary>
        internal bool EscapeReviewDeferred { get; init; }

        /// <summary>Gets a value indicating whether the bot has not landed a hit for minutes, so the map pays it nothing (see the navigator's barren map handling).</summary>
        internal bool Barren { get; init; }

        /// <summary>Gets a value indicating whether the warp cooldown has passed, so the bot may move.</summary>
        internal bool WarpCooldownElapsed { get; init; }
    }

    /// <summary>
    /// The plan of a map move: whether the bot has to escape its current map, whether easier ground is
    /// acceptable, and which monster level a destination has to offer.
    /// </summary>
    /// <param name="Escape">Whether the bot has to leave its current map regardless of its strength.</param>
    /// <param name="StepDown">Whether easier ground than the current one is acceptable (a barren map).</param>
    /// <param name="AllowTrivialHome">Whether going to the class hometown is acceptable even though it offers trivial monsters (the current map holds nothing safe to fight).</param>
    /// <param name="Trivial">Whether the current ground is trivial for the bot, so the review is deferred when nothing better is found.</param>
    /// <param name="EscapeFloor">The lowest monster level an escape target has to offer (see <see cref="MapFit.EscapeTargetFloor"/>).</param>
    /// <param name="Reason">Why the bot considers moving: the text of the bot log.</param>
    internal readonly record struct MapMovePlan(bool Escape, bool StepDown, bool AllowTrivialHome, bool Trivial, int EscapeFloor, MapMoveReason Reason);
}
