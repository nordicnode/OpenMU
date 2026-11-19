// <copyright file="BotChatChannel.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The channels of the bot chat which have their own message budget and statistics. A game server
/// counts them separately, so a burst of normal chat can't use up the whispers' budget, or vice
/// versa.
/// </summary>
internal enum BotChatChannel
{
    /// <summary>
    /// The normal chat around a bot: comments, event reminders, merchant adverts and conversations.
    /// </summary>
    Normal,

    /// <summary>
    /// The party chat of the party a bot is in.
    /// </summary>
    Party,

    /// <summary>
    /// Whispers to a single player.
    /// </summary>
    Whisper,

    /// <summary>
    /// Server-wide <c>/post</c> messages, e.g. about an opened event.
    /// </summary>
    Post,
}
