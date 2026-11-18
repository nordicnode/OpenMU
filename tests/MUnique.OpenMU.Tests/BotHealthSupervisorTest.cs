// <copyright file="BotHealthSupervisorTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests the per-account health record of a bot: when it asks for a restart, when it is quarantined instead of
/// restarted again, and what the operator surfaces get to see.
/// </summary>
[TestFixture]
public class BotHealthSupervisorTest
{
    private readonly ManualTimeProvider _timeProvider = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// A tick failing now and then is skipped; the request for a restart is raised exactly once, at the threshold.
    /// </summary>
    [Test]
    public void AsksForARestartExactlyAtTheThreshold()
    {
        var supervisor = this.CreateSupervisor();

        for (var i = 1; i < BotHealthPolicy.ConsecutiveFaultsUntilRestart; i++)
        {
            Assert.That(supervisor.RecordFault(new NullReferenceException()), Is.False, $"fault #{i} asked for a restart");
        }

        Assert.That(supervisor.RecordFault(new NullReferenceException()), Is.True);
        Assert.That(supervisor.RecordFault(new NullReferenceException()), Is.False);
        Assert.That(supervisor.ConsecutiveFaults, Is.EqualTo(BotHealthPolicy.ConsecutiveFaultsUntilRestart + 1));
    }

    /// <summary>
    /// One successful tick proves the bot is not broken, so the count starts over.
    /// </summary>
    [Test]
    public void ASuccessForgetsTheConsecutiveFaults()
    {
        var supervisor = this.CreateSupervisor();

        for (var i = 0; i < BotHealthPolicy.ConsecutiveFaultsUntilRestart - 1; i++)
        {
            supervisor.RecordFault(new NullReferenceException());
        }

        supervisor.RecordTickSucceeded();
        Assert.That(supervisor.ConsecutiveFaults, Is.Zero);
        Assert.That(supervisor.RecordFault(new NullReferenceException()), Is.False);
    }

    /// <summary>
    /// The end of a session is not a failure - otherwise every shutdown would look like a broken bot.
    /// </summary>
    [Test]
    public void CancellationsAreIgnored()
    {
        var supervisor = this.CreateSupervisor();

        for (var i = 0; i < 100; i++)
        {
            Assert.That(supervisor.RecordFault(new OperationCanceledException()), Is.False);
        }

        Assert.That(supervisor.Faults, Is.Zero);
        Assert.That(supervisor.ConsecutiveFaults, Is.Zero);
        Assert.That(supervisor.IgnoredTicks, Is.EqualTo(100));
    }

    /// <summary>
    /// Transient faults count like the others - a bot which cannot tick is useless either way - but the counters
    /// tell them apart, so an operator can see that this is the database and not the attribute graph.
    /// </summary>
    [Test]
    public void TransientFaultsAreCountedApart()
    {
        var supervisor = this.CreateSupervisor();

        for (var i = 0; i < BotHealthPolicy.ConsecutiveFaultsUntilRestart; i++)
        {
            supervisor.RecordFault(new TimeoutException("the database went away"));
        }

        Assert.That(supervisor.Faults, Is.EqualTo(BotHealthPolicy.ConsecutiveFaultsUntilRestart));
        Assert.That(supervisor.TransientFaults, Is.EqualTo(BotHealthPolicy.ConsecutiveFaultsUntilRestart));
        Assert.That(supervisor.LastFault, Does.Contain("transient"));
    }

    /// <summary>
    /// A bot which breaks itself right after every restart is quarantined instead of restarted again - and stays
    /// quarantined while the quarantine runs.
    /// </summary>
    [Test]
    public void QuarantinesABotWhichKeepsBreakingItself()
    {
        var supervisor = this.CreateSupervisor();

        for (var i = 1; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            Assert.That(supervisor.TryBeginRestart(), Is.True);
            Assert.That(supervisor.RemainingQuarantine(), Is.EqualTo(TimeSpan.Zero), $"restart #{i} quarantined the bot");
        }

        Assert.That(supervisor.TryBeginRestart(), Is.True);
        Assert.That(supervisor.RemainingQuarantine(), Is.EqualTo(BotHealthPolicy.BaseQuarantine));
        Assert.That(supervisor.TryBeginRestart(), Is.False);
        Assert.That(supervisor.Restarts, Is.EqualTo(BotHealthPolicy.RestartsUntilQuarantine));
    }

    /// <summary>
    /// The quarantine ends by itself; the bot is then restarted again - and the next quarantine is longer, because
    /// the restart before it did not stick.
    /// </summary>
    [Test]
    public void TheNextQuarantineAfterAFailedHealIsLonger()
    {
        var supervisor = this.CreateSupervisor();
        for (var i = 0; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            supervisor.TryBeginRestart();
        }

        this._timeProvider.Advance(BotHealthPolicy.BaseQuarantine + TimeSpan.FromSeconds(1));
        Assert.That(supervisor.RemainingQuarantine(), Is.EqualTo(TimeSpan.Zero));
        Assert.That(supervisor.TryBeginRestart(), Is.True);
        Assert.That(supervisor.RemainingQuarantine(), Is.EqualTo(BotHealthPolicy.BaseQuarantine * 2));
    }

    /// <summary>
    /// A bot which ticks without faults for long enough is forgiven its restarts, so a bot which healed keeps a
    /// clean record instead of carrying an escalation forever.
    /// </summary>
    [Test]
    public void AHealthyPeriodEndsTheRestartStreak()
    {
        var supervisor = this.CreateSupervisor();
        for (var i = 0; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            supervisor.TryBeginRestart();
        }

        this._timeProvider.Advance(BotHealthPolicy.BaseQuarantine + TimeSpan.FromSeconds(1));
        this._timeProvider.Advance(BotHealthPolicy.StreakForgiveness + TimeSpan.FromSeconds(1));
        supervisor.RecordTickSucceeded();
        Assert.That(supervisor.TryBeginRestart(), Is.True);
        Assert.That(supervisor.RemainingQuarantine(), Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>
    /// A bot which keeps failing stops writing an error line per tick, and starts over once it ticks again.
    /// </summary>
    [Test]
    public void AFailureStreakIsLoggedOnlyAtItsStart()
    {
        var supervisor = this.CreateSupervisor();

        for (var i = 0; i < BotHealthPolicy.FaultsLoggedAtError; i++)
        {
            Assert.That(supervisor.ShouldLogFault(), Is.True, $"failure #{i + 1}");
            supervisor.RecordFault(new NullReferenceException());
        }

        Assert.That(supervisor.ShouldLogFault(), Is.False);
        supervisor.RecordFault(new NullReferenceException());
        Assert.That(supervisor.ShouldLogFault(), Is.False);

        supervisor.RecordTickSucceeded();
        Assert.That(supervisor.ShouldLogFault(), Is.True);
    }

    /// <summary>
    /// The quarantine report is logged once, so a pass which visits the bot every second doesn't flood the log.
    /// </summary>
    [Test]
    public void TheQuarantineIsReportedOnce()
    {
        var supervisor = this.CreateSupervisor();
        for (var i = 0; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            supervisor.TryBeginRestart();
        }

        Assert.That(supervisor.TakeQuarantineReport(), Is.Not.Null.And.Contain("quarantined"));
        Assert.That(supervisor.TakeQuarantineReport(), Is.Null);
    }

    /// <summary>
    /// A healthy account has nothing to say; one which faulted says what happened, in the line the operator
    /// surfaces show.
    /// </summary>
    [Test]
    public void DescribesOnlyWhatHappened()
    {
        var supervisor = this.CreateSupervisor();
        Assert.That(supervisor.Describe(), Is.Null);

        supervisor.RecordFault(new NullReferenceException("Object reference not set to an instance of an object."));
        supervisor.TryBeginRestart();

        var description = supervisor.Describe();
        Assert.That(description, Does.Contain("1 faulted tick"));
        Assert.That(description, Does.Contain("corrupting"));
        Assert.That(description, Does.Contain("NullReferenceException"));
        Assert.That(description, Does.Contain("restarted 1x"));
    }

    /// <summary>
    /// The per-account record is dropped once it has nothing left to remember - the map is keyed by account and
    /// would otherwise grow with every account which ever ran a bot. A quarantine outlives the idle window.
    /// </summary>
    [Test]
    public void ForgetsIdleAccountsButNotQuarantinedOnes()
    {
        var supervisor = this.CreateSupervisor();
        supervisor.TakeQuarantineReport();
        Assert.That(supervisor.CanForget(), Is.True);

        supervisor.RecordFault(new NullReferenceException());
        Assert.That(supervisor.CanForget(), Is.False);

        this._timeProvider.Advance(BotHealthPolicy.ForgetAfter + TimeSpan.FromSeconds(1));
        Assert.That(supervisor.CanForget(), Is.True);

        for (var i = 0; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            supervisor.TryBeginRestart();
        }

        Assert.That(supervisor.CanForget(), Is.False, "a quarantined bot must keep its record");
        this._timeProvider.Advance(BotHealthPolicy.ForgetAfter + BotHealthPolicy.MaximumQuarantine);
        Assert.That(supervisor.CanForget(), Is.True);
    }

    private BotHealthSupervisor CreateSupervisor() => new(this._timeProvider);

    /// <summary>
    /// A time provider whose time is advanced by the test, so the quarantine and the forgiveness window are exact.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => this._utcNow;

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        /// <param name="timeSpan">The time to advance.</param>
        public void Advance(TimeSpan timeSpan) => this._utcNow += timeSpan;
    }
}
