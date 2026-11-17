// <copyright file="MapFit.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Judges how well the hunting ground a map offers fits a bot's level, and which ground an escape move
/// away from the current map is allowed to target.
/// </summary>
/// <remarks>
/// The rules are deliberately pure functions on monster levels, so the map choice can be reasoned about
/// and tested without a running world. The levels themselves are judged by <see cref="Offline.CombatHandler.IsSafeTarget"/>,
/// which weighs a monster's real damage, defense and health against the bot's own - a monster's nominal
/// level says little about what it does to a character.
/// </remarks>
internal static class MapFit
{
    /// <summary>
    /// A map whose strongest monster the bot can safely fight is below a 1/<see cref="TrivialLevelDivisor"/>
    /// of its level is trivial ground: it still provides experience and loot, but far too little to be
    /// worth the hunt, and too little to pay for the fare of a warp.
    /// </summary>
    internal const int TrivialLevelDivisor = 2;

    /// <summary>
    /// Determines whether the best ground a map offers is trivial for a bot of the given level.
    /// </summary>
    /// <param name="bestSafeMonsterLevel">The level of the strongest monster on the map the bot can safely fight; 0 if it has none.</param>
    /// <param name="botLevel">The bot's reset-aware effective level (see <see cref="BotResetHandler.GetEffectiveLevel"/>).</param>
    /// <returns><c>true</c>, if the map cannot feed the bot anything worth hunting.</returns>
    internal static bool IsTrivial(int bestSafeMonsterLevel, int botLevel)
        => bestSafeMonsterLevel > 0 && bestSafeMonsterLevel * TrivialLevelDivisor < botLevel;

    /// <summary>
    /// Gets the lowest monster level the ground of an escape target must offer to be worth the trip.
    /// </summary>
    /// <param name="currentLevel">The level of the strongest monster the bot can safely fight where it stands; 0 if it has none there.</param>
    /// <param name="mayNotStayAnyway">
    /// Whether the bot has to leave regardless of what the target offers: its current ground is hostile
    /// (nothing it can safely fight) or it may not legally be on that map at all.
    /// </param>
    /// <returns>The floor for the target's monster level.</returns>
    /// <remarks>
    /// A bot only pays a fare (or walks a map gate) to end up on ground at least as good as the ground it
    /// leaves, and when leaving trivial ground it demands a real step up. Without the floor, an escape
    /// meant to take a high-level bot off the starter map would send it straight back into the very same
    /// level band - from Lorencia to Noria, Devias and Elvenland and back - because "anything safe beats
    /// staying" is true for every one of them. The bot then spends its life in the starter maps and never
    /// farms long enough anywhere to earn its way up (the reported "level 200-300 bots farming level-1
    /// monsters in Lorencia").
    /// </remarks>
    internal static int EscapeTargetFloor(int currentLevel, bool mayNotStayAnyway)
        => mayNotStayAnyway || currentLevel <= 0 ? 1 : currentLevel + 1;
}
