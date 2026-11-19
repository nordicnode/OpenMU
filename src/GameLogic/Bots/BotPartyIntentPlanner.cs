// <copyright file="BotPartyIntentPlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Reads a bot's current goal from its state. Pure decision logic - the party composer groups by it.
/// </summary>
internal static class BotPartyIntentPlanner
{
    /// <summary>
    /// Gets what the bot wants from a party right now. A running market shift outranks everything else,
    /// because the merchant is already behind its store and needs its escort; then an event run outranks
    /// a boss attempt, because a published meeting place and a running event are more time-critical than
    /// an invasion.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The intent; <see cref="BotPartyIntent.Experience"/> when the bot has no special goal.</returns>
    internal static BotPartyIntent GetIntent(OfflinePlayer bot, DateTime now)
    {
        if (bot.MarketShift is { } shift && now < shift.UntilUtc)
        {
            return new BotPartyIntent(BotPartyGoal.MarketEscort, shift.Map, shift.UntilUtc);
        }

        if (bot.EventMeeting is { Event: { } miniGame } meeting && now < meeting.UntilUtc)
        {
            return new BotPartyIntent(BotPartyGoal.EventRun, meeting.Map, meeting.UntilUtc, miniGame);
        }

        if (bot.BossAttempt is { } bossAttempt && now < bossAttempt.UntilUtc)
        {
            return new BotPartyIntent(BotPartyGoal.BossAttempt, bossAttempt.Map, bossAttempt.UntilUtc);
        }

        return BotPartyIntent.Experience;
    }
}
