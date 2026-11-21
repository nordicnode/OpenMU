// <copyright file="LiveBotMeeting.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>A bot's active meeting.</summary>
/// <param name="State">The meeting state.</param>
/// <param name="Place">The meeting location.</param>
/// <param name="Event">The event name, if any.</param>
/// <param name="EndsAtUtc">The end of the meeting.</param>
public sealed record LiveBotMeeting(string State, string Place, string? Event, DateTime EndsAtUtc);
