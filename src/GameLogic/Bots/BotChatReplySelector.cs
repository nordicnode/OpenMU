// <copyright file="BotChatReplySelector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Picks the line a bot says or answers with, without repeating the line it said recently or the one
/// the same player got last time. The memory lives in the <see cref="BotChatThrottle"/>. Pure logic -
/// exposed for unit tests.
/// </summary>
internal static class BotChatReplySelector
{
    /// <summary>
    /// Picks a line of the list, other than the lines the bot said recently and the one the listener
    /// of this list got last time.
    /// </summary>
    /// <param name="throttle">The chat throttle which knows the recent lines.</param>
    /// <param name="bot">The bot which speaks.</param>
    /// <param name="lines">The lines to choose from.</param>
    /// <param name="listener">The player who listens, for the line memory per player; <c>null</c> for an unasked line.</param>
    /// <returns>The picked line, or <c>null</c> when the list has no usable line.</returns>
    /// <remarks>
    /// Picking doesn't touch the memory: the caller records the line with <see cref="RememberLine"/>
    /// once it was reserved for sending. A line which a budget, a mute or a cooldown denies must not
    /// use up a slot of the recent-line memory.
    /// </remarks>
    internal static BotChatLine? PickLineFor(BotChatThrottle throttle, Player bot, ICollection<BotChatLine> lines, Player? listener = null)
        => BotChatComposer.PickCurrentLine(
            lines,
            listener is null ? null : throttle.GetLastLine(listener, lines),
            throttle.GetRecentLines(bot, lines));

    /// <summary>
    /// Records a line as said: the bot won't repeat it while the list has unseen lines, and the
    /// listener won't get it again right away.
    /// </summary>
    /// <param name="throttle">The chat throttle which remembers the lines.</param>
    /// <param name="bot">The bot which said the line.</param>
    /// <param name="lines">The list the line belongs to.</param>
    /// <param name="line">The said line.</param>
    /// <param name="listener">The player who heard it, for the line memory per player; <c>null</c> for an unasked line.</param>
    internal static void RememberLine(BotChatThrottle throttle, Player bot, ICollection<BotChatLine> lines, BotChatLine line, Player? listener = null)
    {
        if (listener is not null)
        {
            throttle.SetLastLine(listener, lines, line);
        }

        throttle.RememberLine(bot, lines, line);
    }

    /// <summary>
    /// Picks the answer of a bot behind its open store on the market to a message which asks what it sells;
    /// <c>null</c> for any other bot or message.
    /// </summary>
    /// <param name="bot">The bot which may answer.</param>
    /// <param name="throttle">The chat throttle which knows the recent replies.</param>
    /// <param name="listener">The player who asked.</param>
    /// <param name="message">The player's message.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The reply, or <c>null</c> for any other bot or message.</returns>
    internal static BotChatReply? PickMerchantReplyFor(Player bot, BotChatThrottle throttle, Player listener, string message, BotChatConfiguration configuration)
        => bot is OfflinePlayer { MarketShift: { IsStoreOpened: true, IsEnding: false } }
            ? PickReplyFor(throttle, bot, listener, configuration.MerchantWhisperReplies, message, requireKeyword: true)
            : null;

    /// <summary>
    /// Picks the reply to the player's message, other than the replies the bot gave recently and the
    /// reply the player got last time.
    /// </summary>
    /// <param name="throttle">The chat throttle which knows the recent replies.</param>
    /// <param name="bot">The bot which answers.</param>
    /// <param name="listener">The player who wrote the message.</param>
    /// <param name="replies">The replies to choose from.</param>
    /// <param name="message">The player's message.</param>
    /// <param name="requireKeyword">Whether only a reply with a matching keyword may be picked.</param>
    /// <returns>The reply, or <c>null</c> when none fits.</returns>
    /// <remarks>
    /// Picking doesn't touch the memory: the caller records the reply with <see cref="RememberReply"/>
    /// once it was reserved for sending.
    /// </remarks>
    internal static BotChatReply? PickReplyFor(BotChatThrottle throttle, Player bot, Player listener, ICollection<BotChatReply> replies, string message, bool requireKeyword = false)
        => BotChatComposer.PickWhisperReply(
            replies,
            message,
            count => Rand.NextInt(0, count),
            throttle.GetLastLine(listener, replies),
            requireKeyword,
            throttle.GetRecentLines(bot, replies));

    /// <summary>
    /// Records a reply as said: the bot won't repeat it while it has unseen ones, and the player won't
    /// get it again right away.
    /// </summary>
    /// <param name="throttle">The chat throttle which remembers the replies.</param>
    /// <param name="bot">The bot which said the reply.</param>
    /// <param name="listener">The player who got the reply.</param>
    /// <param name="replies">The list the reply belongs to.</param>
    /// <param name="reply">The said reply.</param>
    internal static void RememberReply(BotChatThrottle throttle, Player bot, Player listener, ICollection<BotChatReply> replies, BotChatReply reply)
    {
        throttle.SetLastLine(listener, replies, reply);
        throttle.RememberLine(bot, replies, reply);
    }
}
