// <copyright file="BotStateMachine.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;
using System.Text;

/// <summary>
/// Keeps track of which <see cref="BotState"/> a bot is in, since when, and why - and of how much time it
/// spent in each state, so that a mode which looks wrong can be told apart from a mode which is just
/// taking long.
/// </summary>
/// <remarks>
/// The machine observes the navigation tick instead of driving it: the branch of
/// <see cref="BotNavigator"/> which claims a tick reports the state it put the bot in, so the recorded
/// state is a fact about that tick rather than a second opinion which could disagree with the bot's
/// behaviour. Two guards keep the bookkeeping honest:
/// <list type="bullet">
/// <item>a transition needs a reason - a state without one is a bug in the reporting call, not a state;</item>
/// <item>a routine state (hunting, traveling, following, idle) must not be entered while the player still
/// carries the flags of an unfinished errand. That would mean the navigation left an errand half-done,
/// which is what <see cref="GuardViolations"/> counts.</item>
/// </list>
/// Re-entering the current state does not count as a transition (and leaves the duration running); it only
/// refreshes the reason, so a bot which hunts for an hour reports the newest reason it is hunting.
/// </remarks>
internal sealed class BotStateMachine
{
    /// <summary>How many transitions <see cref="RecentTransitions"/> keeps.</summary>
    internal const int HistorySize = 16;

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Tells whether the player still carries the flags of an unfinished errand while the bot is about to
    /// enter the given state (see <see cref="GuardViolations"/>).
    /// </summary>
    private readonly Func<BotState, bool> _isErrandClaimed;

    private readonly Dictionary<BotState, int> _entered = new();
    private readonly Dictionary<BotState, TimeSpan> _spent = new();
    private readonly Queue<BotStateTransition> _history = new();

    private DateTimeOffset _sinceUtc;
    private string _reason;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotStateMachine"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider which stamps the transitions.</param>
    /// <param name="isErrandClaimed">
    /// Tells whether the flags of an unfinished errand are still set on the player for the given state.
    /// </param>
    internal BotStateMachine(TimeProvider timeProvider, Func<BotState, bool> isErrandClaimed)
    {
        this._timeProvider = timeProvider;
        this._isErrandClaimed = isErrandClaimed;
        this._sinceUtc = timeProvider.GetUtcNow();
        this._reason = "started";
        this._entered[BotState.Idle] = 1;
    }

    /// <summary>
    /// Gets the state the bot is in right now.
    /// </summary>
    internal BotState Current { get; private set; }

    /// <summary>
    /// Gets the reason the current state was entered with.
    /// </summary>
    internal string CurrentReason => this._reason;

    /// <summary>
    /// Gets the number of routine states the bot was put into while an errand was still unfinished - each
    /// one a sign that a branch of the navigation bypassed the errand it left running.
    /// </summary>
    internal int GuardViolations { get; private set; }

    /// <summary>
    /// Gets the number of ticks which reported the state the bot was already in.
    /// </summary>
    internal int Reentries { get; private set; }

    /// <summary>
    /// Reports the state the current tick put the bot in.
    /// </summary>
    /// <param name="next">The state the tick put the bot in.</param>
    /// <param name="reason">Why the tick put it there.</param>
    /// <exception cref="ArgumentException">Thrown when no reason is given.</exception>
    internal void Enter(BotState next, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = this._timeProvider.GetUtcNow();
        if (next == this.Current)
        {
            this.Reentries++;
            this._reason = reason;
            return;
        }

        if (this._isErrandClaimed(next))
        {
            this.GuardViolations++;
        }

        this._spent[this.Current] = this.SpentOf(this.Current, now);
        this._entered[next] = this.EnteredCount(next) + 1;
        this._history.Enqueue(new BotStateTransition(this.Current, next, now, reason));
        while (this._history.Count > HistorySize)
        {
            this._history.Dequeue();
        }

        this.Current = next;
        this._sinceUtc = now;
        this._reason = reason;
    }

    /// <summary>
    /// Gets how long the bot has been in the current state.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The time the bot spent in the current state so far.</returns>
    internal TimeSpan TimeInCurrent(DateTimeOffset now) => now - this._sinceUtc;

    /// <summary>
    /// Gets how much time the bot spent in the state in total, including the current stay.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The time the bot spent in the state.</returns>
    internal TimeSpan SpentIn(BotState state, DateTimeOffset now) => this.SpentOf(state, now);

    /// <summary>
    /// Gets how often the bot entered the state (not counting ticks which kept it).
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The number of transitions into the state.</returns>
    internal int EnteredCount(BotState state) => this._entered.GetValueOrDefault(state);

    /// <summary>
    /// Gets the most recent transitions, oldest first.
    /// </summary>
    /// <returns>The recent transitions.</returns>
    internal IReadOnlyList<BotStateTransition> RecentTransitions() => this._history.ToArray();

    /// <summary>
    /// Summarizes the state of the bot in one line - the state, how long it has been in it and why - for the
    /// projections which have room for a single field, like the live player list of the admin panel.
    /// </summary>
    /// <returns>The one-line summary.</returns>
    internal string Summarize()
    {
        var now = this._timeProvider.GetUtcNow();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} for {1} ({2})",
            this.Current,
            Format(this.TimeInCurrent(now)),
            this._reason);
    }

    /// <summary>
    /// Describes the state of the bot in the few lines a game master asks for when a bot looks wedged, idle
    /// or never joins an event: what it does, since when, why, where the time went and what it did before.
    /// </summary>
    /// <returns>The description.</returns>
    internal string Describe()
    {
        var now = this._timeProvider.GetUtcNow();
        var builder = new StringBuilder();
        builder.Append("state ").Append(this.Current)
            .Append(" for ").Append(Format(this.TimeInCurrent(now)))
            .Append(" (").Append(this._reason).Append(')');

        if (this.GuardViolations > 0)
        {
            builder.Append(", errand bypassed ").Append(this.GuardViolations).Append('x');
        }

        var spent = Enum.GetValues<BotState>()
            .Select(state => (State: state, Time: this.SpentOf(state, now)))
            .Where(x => x.Time > TimeSpan.Zero)
            .OrderByDescending(x => x.Time)
            .Select(x => x.State + " " + Format(x.Time))
            .ToList();
        if (spent.Count > 0)
        {
            builder.Append('\n').Append("time: ").Append(string.Join(", ", spent));
        }

        var recent = this._history.Reverse().Take(4).Select(BotStateTransitionFormat.Format).ToList();
        if (recent.Count > 0)
        {
            builder.Append('\n').Append("last: ").Append(string.Join(" | ", recent));
        }

        return builder.ToString();
    }

    private static string Format(TimeSpan duration)
        => duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private TimeSpan SpentOf(BotState state, DateTimeOffset now)
        => this._spent.GetValueOrDefault(state) + (state == this.Current ? now - this._sinceUtc : TimeSpan.Zero);
}
