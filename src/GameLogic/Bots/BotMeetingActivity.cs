// <copyright file="BotMeetingActivity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;

/// <summary>
/// The event meeting a bot is currently on, in the shape the administration surfaces report it: whether the
/// bot has only announced it, is traveling to the place or already waits there, where that place is and when
/// the meeting ends. It tells an operator why a bot which promised to come isn't there yet, or how long it is
/// still worth waiting for it.
/// </summary>
/// <param name="State">The state of the meeting, e.g. <c>Waiting</c>.</param>
/// <param name="Place">The map and the NPC or coordinates the bot meets at; the map with <c>(safezone)</c>
/// when it waits anywhere in the safezone.</param>
/// <param name="Event">The mini game whose announced entrance the meeting belongs to, or <c>null</c> when a
/// player named the place.</param>
/// <param name="EndsAtUtc">When the meeting stops being valid, i.e. when the entrance closes or the agreed
/// wait time is over. The remaining time is derived from it at the time it is read, so a surface which
/// re-renders shows a countdown which actually moves.</param>
public sealed record BotMeetingActivity(
    string State,
    string Place,
    string? Event,
    DateTime EndsAtUtc)
{
    /// <summary>
    /// Gets how long the meeting is still valid, measured at the current time.
    /// </summary>
    public TimeSpan TimeRemaining => this.RemainingAt(DateTime.UtcNow);

    /// <summary>
    /// Gets the meeting in one line: its state, the place and the remaining wait time - the second line of
    /// the state cell in the bot table.
    /// </summary>
    public string Summary => this.SummaryAt(DateTime.UtcNow);

    /// <summary>
    /// Gets the long form for the tooltip and the state command: the state, the place, the event it belongs
    /// to and the remaining wait time.
    /// </summary>
    public string Detail => this.DetailAt(DateTime.UtcNow);

    private string StateAction
        => this.State switch
        {
            nameof(BotMeetingState.Announced) => "Announced at",
            nameof(BotMeetingState.Traveling) => "Traveling to",
            nameof(BotMeetingState.Waiting) => "Waiting at",
            _ => "Meeting at",
        };

    private string StateDescription
        => this.State switch
        {
            nameof(BotMeetingState.Announced) => "announced",
            nameof(BotMeetingState.Traveling) => "traveling",
            nameof(BotMeetingState.Waiting) => "waiting",
            _ => this.State,
        };

    /// <summary>
    /// Gets the remaining wait time at the given time.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <returns>The time until the meeting ends; zero when it already ran out.</returns>
    internal TimeSpan RemainingAt(DateTime utcNow)
        => this.EndsAtUtc > utcNow ? this.EndsAtUtc - utcNow : TimeSpan.Zero;

    /// <summary>
    /// Gets the one-line summary at the given time, for tests and for callers which already have the clock.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <returns>The one-line summary.</returns>
    internal string SummaryAt(DateTime utcNow)
        => $"{this.StateAction} {this.Place}, {BotStateMachine.FormatDuration(this.RemainingAt(utcNow))} left";

    /// <summary>
    /// Gets the long form at the given time, for tests and for callers which already have the clock.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <returns>The long form.</returns>
    internal string DetailAt(DateTime utcNow)
        => this.Event is { } miniGame
            ? $"meeting {this.StateDescription} at {this.Place} ({miniGame}), {BotStateMachine.FormatDuration(this.RemainingAt(utcNow))} left"
            : $"meeting {this.StateDescription} at {this.Place}, {BotStateMachine.FormatDuration(this.RemainingAt(utcNow))} left";

    /// <summary>
    /// Projects the meeting of a bot for the administration surfaces, or returns <c>null</c> while it has
    /// none. A nameless custom map is reported by its number, so the place is never an empty cell.
    /// </summary>
    /// <param name="meeting">The meeting of the bot, or <c>null</c>.</param>
    /// <returns>The meeting as the surfaces report it, or <c>null</c>.</returns>
    internal static BotMeetingActivity? From(BotEventMeeting? meeting)
    {
        if (meeting is null)
        {
            return null;
        }

        var place = BotChatComposer.FormatLocation(meeting.Map.Name.ValueInNeutralLanguage, meeting.NpcPosition);
        if (place.Length == 0)
        {
            place = string.Create(CultureInfo.InvariantCulture, $"map {meeting.Map.Number}");
        }

        if (meeting.NpcPosition is null)
        {
            place += " (safezone)";
        }

        return new BotMeetingActivity(
            meeting.State.ToString(),
            place,
            meeting.Event?.Name.ValueInNeutralLanguage,
            meeting.UntilUtc);
    }
}
