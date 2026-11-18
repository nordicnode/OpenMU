// <copyright file="BotStateMachineTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;
using NUnit.Framework;

/// <summary>
/// Tests for the bookkeeping of what a bot does: the state it reports, how long it has been in it, how often
/// it entered each state, what it did before, and the guard which catches a routine state entered while an
/// errand is still unfinished (see <see cref="BotStateMachine"/>).
/// </summary>
/// <remarks>
/// The machine observes the navigation tick, so the tests report states the way the tick does - one
/// <see cref="BotStateMachine.Enter"/> per tick - and check what the game master's state command would say.
/// </remarks>
[TestFixture]
public class BotStateMachineTest
{
    private ManualTimeProvider _timeProvider = null!;

    /// <summary>
    /// Sets up the time provider the machine stamps its transitions with.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        this._timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A bot which has not reported anything yet waits, and says so.
    /// </summary>
    [Test]
    public void StartsIdle()
    {
        var machine = CreateMachine(this._timeProvider);

        Assert.That(machine.Current, Is.EqualTo(BotState.Idle));
        Assert.That(machine.Describe(), Does.Contain("state Idle"));
    }

    /// <summary>
    /// Reporting a state counts the way into it and starts its clock.
    /// </summary>
    [Test]
    public void ReportsTheStateAndItsDuration()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Hunt, "monsters nearby");
        this._timeProvider.Advance(TimeSpan.FromSeconds(30));

        Assert.That(machine.Current, Is.EqualTo(BotState.Hunt));
        Assert.That(machine.CurrentReason, Is.EqualTo("monsters nearby"));
        Assert.That(machine.EnteredCount(BotState.Hunt), Is.EqualTo(1));
        Assert.That(machine.TimeInCurrent(this._timeProvider.GetUtcNow()), Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(machine.SpentIn(BotState.Hunt, this._timeProvider.GetUtcNow()), Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// A bot which keeps hunting for an hour reports the newest reason without restarting the clock - ticks
    /// repeat their state all the time, and every repeat counting as a transition would make the counts
    /// meaningless.
    /// </summary>
    [Test]
    public void RepeatingTheStateKeepsTheClockRunning()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Hunt, "monsters nearby");
        this._timeProvider.Advance(TimeSpan.FromSeconds(10));
        machine.Enter(BotState.Hunt, "monsters nearby, walking to one");

        Assert.That(machine.Current, Is.EqualTo(BotState.Hunt));
        Assert.That(machine.EnteredCount(BotState.Hunt), Is.EqualTo(1));
        Assert.That(machine.Reentries, Is.EqualTo(1));
        Assert.That(machine.TimeInCurrent(this._timeProvider.GetUtcNow()), Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(machine.CurrentReason, Is.EqualTo("monsters nearby, walking to one"));
    }

    /// <summary>
    /// The time is booked to the state it was spent in, and a state entered twice counts twice.
    /// </summary>
    [Test]
    public void BooksTheTimeToTheStateItWasSpentIn()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Hunt, "monsters nearby");
        this._timeProvider.Advance(TimeSpan.FromSeconds(10));
        machine.Enter(BotState.Travel, "walking to the ground");
        this._timeProvider.Advance(TimeSpan.FromSeconds(5));
        machine.Enter(BotState.Hunt, "arrived and fighting");

        Assert.That(machine.SpentIn(BotState.Hunt, this._timeProvider.GetUtcNow()), Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(machine.SpentIn(BotState.Travel, this._timeProvider.GetUtcNow()), Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(machine.EnteredCount(BotState.Hunt), Is.EqualTo(2));
        Assert.That(machine.EnteredCount(BotState.Travel), Is.EqualTo(1));
    }

    /// <summary>
    /// The current stay is included in the time a state has, so the totals in the description always add up
    /// to the time the bot has been running.
    /// </summary>
    [Test]
    public void TheCurrentStayCountsTowardItsState()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Travel, "walking to the ground");
        this._timeProvider.Advance(TimeSpan.FromMinutes(2));

        Assert.That(machine.SpentIn(BotState.Travel, this._timeProvider.GetUtcNow()), Is.EqualTo(TimeSpan.FromMinutes(2)));
    }

    /// <summary>
    /// The duration a bot reports measures the current stay on the machine's own clock, which is what the
    /// administration surfaces sort the bots by - the one stuck longest first.
    /// </summary>
    [Test]
    public void ReportsHowLongTheCurrentStayLasted()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Idle, "walk in progress");
        this._timeProvider.Advance(TimeSpan.FromSeconds(83));

        Assert.That(machine.CurrentDuration, Is.EqualTo(TimeSpan.FromSeconds(83)));
    }

    /// <summary>
    /// A transition without a reason is a bug in the reporting call, not a state - it is rejected instead of
    /// being recorded as an unexplained change.
    /// </summary>
    [Test]
    public void RejectsATransitionWithoutAReason()
    {
        var machine = CreateMachine(this._timeProvider);

        Assert.That(() => machine.Enter(BotState.Hunt, " "), Throws.ArgumentException);
        Assert.That(machine.Current, Is.EqualTo(BotState.Idle));
    }

    /// <summary>
    /// A routine state entered while the player still carries the flags of an unfinished errand means a
    /// navigation branch bypassed the errand it left running - the guard counts it, and the description
    /// says so, because that is exactly the "why doesn't it hunt / why doesn't it join" bug hunt.
    /// </summary>
    [Test]
    public void CountsARoutineStateEnteredWhileAnErrandIsUnfinished()
    {
        var machine = new BotStateMachine(this._timeProvider, next => !next.IsErrand() && !next.IsSuspended());

        machine.Enter(BotState.Hunt, "monsters nearby");
        machine.Enter(BotState.Shopping, "town errand: selling and restocking");

        Assert.That(machine.GuardViolations, Is.EqualTo(1));
        Assert.That(machine.Describe(), Does.Contain("errand bypassed 1x"));
    }

    /// <summary>
    /// A clean bot reports no guard line at all, so the description stays short for the normal case.
    /// </summary>
    [Test]
    public void ReportsNoGuardLineForACleanBot()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Hunt, "monsters nearby");
        machine.Enter(BotState.Travel, "walking to the ground");

        Assert.That(machine.GuardViolations, Is.Zero);
        Assert.That(machine.Describe(), Does.Not.Contain("errand bypassed"));
    }

    /// <summary>
    /// The history keeps only the latest transitions - a bot runs for hours, and the command shows what it
    /// did before the current state, not its whole life.
    /// </summary>
    [Test]
    public void KeepsOnlyTheLatestTransitions()
    {
        var machine = CreateMachine(this._timeProvider);

        for (var i = 0; i < (BotStateMachine.HistorySize * 2) + 3; i++)
        {
            machine.Enter(i % 2 == 0 ? BotState.Travel : BotState.Hunt, "walking to the ground " + i);
        }

        var recent = machine.RecentTransitions();
        Assert.That(recent, Has.Count.EqualTo(BotStateMachine.HistorySize));
        Assert.That(recent[^1].To, Is.EqualTo(BotState.Travel));
        Assert.That(recent[^1].Reason, Does.Contain(((BotStateMachine.HistorySize * 2) + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The description names the state, the reason, where the time went and what happened before - what a
    /// game master needs to tell a wedged bot from a slow one.
    /// </summary>
    [Test]
    public void DescribesTheStateTheReasonTheTimeAndTheHistory()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Travel, "walking to the ground");
        this._timeProvider.Advance(TimeSpan.FromSeconds(42));
        machine.Enter(BotState.Hunt, "monsters nearby");
        this._timeProvider.Advance(TimeSpan.FromSeconds(7));

        var description = machine.Describe();
        Assert.That(description, Does.Contain("state Hunt for"));
        Assert.That(description, Does.Contain("(monsters nearby)"));
        Assert.That(description, Does.Contain("time:"));
        Assert.That(description, Does.Contain("Travel 0:42"));
        Assert.That(description, Does.Contain("last:"));
        Assert.That(description, Does.Contain("Travel->Hunt"));
    }

    /// <summary>
    /// The one-line summary names the state, how long the bot has been in it and why - the single field the
    /// live player list of the admin panel has room for.
    /// </summary>
    [Test]
    public void SummarizesTheStateInOneLine()
    {
        var machine = CreateMachine(this._timeProvider);

        machine.Enter(BotState.Hunt, "monsters nearby");
        this._timeProvider.Advance(TimeSpan.FromSeconds(83));

        Assert.That(machine.Summarize(), Is.EqualTo("Hunt for 1:23 (monsters nearby)"));
    }

    /// <summary>
    /// The states which finish before the routine resumes are told apart from the ones which suspend it -
    /// the classification the guard and any reader of the description rely on.
    /// </summary>
    /// <param name="stateValue">The state, as the value NUnit passes (the enum is internal to the game logic).</param>
    /// <param name="isErrand">Whether the state is an errand.</param>
    /// <param name="isSuspended">Whether the state suspends the routine.</param>
    [TestCase(BotState.Shopping, true, false)]
    [TestCase(BotState.Buff, true, false)]
    [TestCase(BotState.ChaosMachine, true, false)]
    [TestCase(BotState.MarketShift, true, false)]
    [TestCase(BotState.EventMeeting, true, false)]
    [TestCase(BotState.Revenge, true, false)]
    [TestCase(BotState.Hunt, false, false)]
    [TestCase(BotState.Travel, false, false)]
    [TestCase(BotState.FollowLeader, false, false)]
    [TestCase(BotState.Recover, false, false)]
    [TestCase(BotState.Idle, false, false)]
    [TestCase(BotState.MiniGame, false, true)]
    [TestCase(BotState.Reset, false, true)]
    public void ClassifiesTheStates(int stateValue, bool isErrand, bool isSuspended)
    {
        var state = (BotState)stateValue;
        Assert.That(state.IsErrand(), Is.EqualTo(isErrand));
        Assert.That(state.IsSuspended(), Is.EqualTo(isSuspended));
    }

    private static BotStateMachine CreateMachine(ManualTimeProvider timeProvider)
        => new(timeProvider, _ => false);

    /// <summary>
    /// A time provider whose time is advanced by the test, so the durations are exact.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        /// <summary>
        /// Initializes a new instance of the <see cref="ManualTimeProvider"/> class.
        /// </summary>
        /// <param name="utcNow">The time to start at.</param>
        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            this._utcNow = utcNow;
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => this._utcNow;

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        /// <param name="timeSpan">The time to advance.</param>
        public void Advance(TimeSpan timeSpan) => this._utcNow += timeSpan;
    }
}
