// <copyright file="LiveBotMeetingOutcome.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>The outcome of a bot's last meeting.</summary>
/// <param name="State">The outcome state.</param>
/// <param name="Place">The meeting location.</param>
/// <param name="Event">The event name, if any.</param>
/// <param name="Reason">The reason, if any.</param>
public sealed record LiveBotMeetingOutcome(string State, string Place, string? Event, string? Reason);
