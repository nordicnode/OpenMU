// <copyright file="BotChatChannelStatistics.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The message counts of one <see cref="BotChatChannel"/>.
/// </summary>
/// <param name="Granted">The number of messages which passed all limits.</param>
/// <param name="Throttled">The number of messages which a cooldown or the channel's budget denied.</param>
/// <param name="Muted">The number of messages which a mute window denied.</param>
internal readonly record struct BotChatChannelStatistics(long Granted, long Throttled, long Muted);
