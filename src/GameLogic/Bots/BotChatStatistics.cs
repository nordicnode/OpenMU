// <copyright file="BotChatStatistics.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// What the bot chat did on one game server since it started: how many messages were allowed,
/// throttled by a cooldown or a budget, and muted, per channel - and how many occasions such as
/// event reminders, greetings or congratulations were suppressed. A game master reads it with
/// <c>/botchat</c> to tell a quiet-but-working chat from one which is held back by its limits.
/// </summary>
/// <param name="Granted">The number of messages which passed all limits.</param>
/// <param name="Throttled">The number of messages which a cooldown or a rate budget denied.</param>
/// <param name="Muted">The number of messages which a mute window denied.</param>
/// <param name="SuppressedOccasions">The number of occasions the bots did not announce because
/// the chat was muted.</param>
/// <param name="Channels">The same numbers for each channel; each channel has its own budget.</param>
/// <param name="MutedUntilUtc">Until when the chat is muted, or <c>null</c> while it is not.</param>
internal sealed record BotChatStatistics(
    long Granted,
    long Throttled,
    long Muted,
    long SuppressedOccasions,
    IReadOnlyDictionary<BotChatChannel, BotChatChannelStatistics> Channels,
    DateTime? MutedUntilUtc);
