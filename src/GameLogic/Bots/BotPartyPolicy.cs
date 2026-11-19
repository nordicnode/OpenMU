// <copyright file="BotPartyPolicy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Shared tuning for bot party formation.
/// </summary>
internal static class BotPartyPolicy
{
    /// <summary>Smallest party size.</summary>
    internal const int MinPartySize = 2;

    /// <summary>Largest party size.</summary>
    internal const int MaxPartySize = 5;

    /// <summary>Widest effective level gap inside a party.</summary>
    internal const int MaxLevelGap = 12;

    /// <summary>Share of candidates grouped into parties.</summary>
    internal const int PartiedSharePercent = 80;

    /// <summary>
    /// Share of the bot's level which its equipped gear score has to reach for an event run. An event
    /// party enters content with a level bracket; a nearly naked bot would only be carried there.
    /// </summary>
    internal const double EventGearScorePerLevel = 0.35;

    /// <summary>
    /// Share of the bot's level which its equipped gear score has to reach for a boss attempt. Higher
    /// than for an event: an invasion fights back with real damage instead of a level bracket.
    /// </summary>
    internal const double BossAttemptGearScorePerLevel = 0.75;

    /// <summary>
    /// Share of the bot's level which its equipped gear score has to reach to guard a merchant. A guard
    /// only has to survive the trip through hostile ground.
    /// </summary>
    internal const double EscortGearScorePerLevel = 0.45;

    /// <summary>
    /// How many guards a merchant gets. More than two turns the escort into a hunting party which
    /// blocks the market safezone.
    /// </summary>
    internal const int MarketEscortGuards = 2;

    /// <summary>
    /// Planning divisor for population sizing. Below the 3.5 target mean:
    /// build key dedupe and level gaps truncate real parties.
    /// </summary>
    internal const double ExpectedPartySize = 3.0;

    /// <summary>
    /// How long an invasion stays a boss-attempt goal after it was announced. The invasion plugins run
    /// for a few minutes; after that the map has nothing left to fight for the group.
    /// </summary>
    internal static readonly TimeSpan BossAttemptDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Estimates the party count for a population.
    /// One buffer per party, so this is also the buffer quota.
    /// </summary>
    /// <param name="botCount">The bot count.</param>
    internal static int EstimatePartyCount(int botCount)
    {
        return (int)Math.Ceiling(botCount * PartiedSharePercent / 100.0 / ExpectedPartySize);
    }
}
