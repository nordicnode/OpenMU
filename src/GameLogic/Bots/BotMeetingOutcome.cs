// <copyright file="BotMeetingOutcome.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// How an event meeting of a bot ended, in the shape the administration surfaces report it: whether the bot
/// was taken into a party, ran out of time or gave up, where that happened, and why - so a bot which promised
/// to come and didn't can be checked after the meeting is over instead of only in the log.
/// </summary>
/// <param name="State">The terminal state the meeting ended in: <c>Joined</c>, <c>Expired</c> or
/// <c>Abandoned</c>.</param>
/// <param name="Place">The map and the NPC or coordinates the meeting was planned at; the map with
/// <c>(safezone)</c> when the bot fell back to waiting anywhere in the safezone.</param>
/// <param name="Event">The mini game whose announced entrance the meeting belonged to, or <c>null</c> when a
/// player named the place.</param>
/// <param name="Reason">Why the meeting was given up, when the state alone doesn't say it; otherwise
/// <c>null</c>.</param>
public sealed record BotMeetingOutcome(
    string State,
    string Place,
    string? Event,
    string? Reason)
{
    /// <summary>
    /// Gets the outcome in one line, e.g. <c>Last meeting: expired at Devias 215, 40</c> - the line under the
    /// state of a bot whose meeting is over.
    /// </summary>
    public string Summary => $"Last meeting: {this.StateDescription} at {this.Place}";

    /// <summary>
    /// Gets the long form for the tooltip, the bot list and the state command: the state, the place, the event
    /// it belonged to and why it ended.
    /// </summary>
    public string Detail
        => this.Event is { } miniGame
            ? $"last meeting {this.StateDescription} at {this.Place} ({miniGame}){this.ReasonSuffix}"
            : $"last meeting {this.StateDescription} at {this.Place}{this.ReasonSuffix}";

    private string StateDescription
        => this.State switch
        {
            nameof(BotMeetingState.Joined) => "joined",
            nameof(BotMeetingState.Expired) => "expired",
            nameof(BotMeetingState.Abandoned) => "abandoned",
            _ => this.State,
        };

    private string ReasonSuffix
        => string.IsNullOrWhiteSpace(this.Reason) ? string.Empty : $" - {this.Reason}";
}
