// <copyright file="BotChatReplySelector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Picks the line a bot answers with, without repeating the line the same player got last time. The
/// memory of that line lives in the <see cref="BotChatThrottle"/>. Pure logic - exposed for unit tests.
/// </summary>
internal static class BotChatReplySelector
{
    /// <summary>
    /// Picks a line said to the player, other than the one of this list the player got last time.
    /// </summary>
    /// <param name="throttle">The chat throttle which remembers the last line.</param>
    /// <param name="listener">The player who listens.</param>
    /// <param name="lines">The lines to choose from.</param>
    /// <returns>The picked line, or <c>null</c> when the list has no usable line.</returns>
    internal static BotChatLine? PickLineFor(BotChatThrottle throttle, Player listener, ICollection<BotChatLine> lines)
    {
        var line = BotChatComposer.PickCurrentLine(lines, throttle.GetLastLine(listener, lines));
        if (line is not null)
        {
            throttle.SetLastLine(listener, lines, line);
        }

        return line;
    }

    /// <summary>
    /// Picks the answer of a bot behind its open store on the market to a message which asks what it sells;
    /// <c>null</c> for any other bot or message.
    /// </summary>
    /// <param name="bot">The bot which may answer.</param>
    /// <param name="throttle">The chat throttle which remembers the last reply.</param>
    /// <param name="listener">The player who asked.</param>
    /// <param name="message">The player's message.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The reply, or <c>null</c> for any other bot or message.</returns>
    internal static BotChatReply? PickMerchantReplyFor(Player bot, BotChatThrottle throttle, Player listener, string message, BotChatConfiguration configuration)
        => bot is OfflinePlayer { MarketShift: { IsStoreOpened: true, IsEnding: false } }
            ? PickReplyFor(throttle, listener, configuration.MerchantWhisperReplies, message, requireKeyword: true)
            : null;

    /// <summary>
    /// Picks the reply to the player's message, other than the reply the player got last time.
    /// </summary>
    /// <param name="throttle">The chat throttle which remembers the last reply.</param>
    /// <param name="listener">The player who wrote the message.</param>
    /// <param name="replies">The replies to choose from.</param>
    /// <param name="message">The player's message.</param>
    /// <param name="requireKeyword">Whether only a reply with a matching keyword may be picked.</param>
    /// <returns>The reply, or <c>null</c> when none fits.</returns>
    internal static BotChatReply? PickReplyFor(BotChatThrottle throttle, Player listener, ICollection<BotChatReply> replies, string message, bool requireKeyword = false)
    {
        var reply = BotChatComposer.PickWhisperReply(replies, message, count => Rand.NextInt(0, count), throttle.GetLastLine(listener, replies), requireKeyword);
        if (reply is not null)
        {
            throttle.SetLastLine(listener, replies, reply);
        }

        return reply;
    }
}
