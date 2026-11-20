// <copyright file="BotHealthSupervisor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Watches one bot account's health: it counts the AI ticks which fail, classifies why they fail, decides when
/// the bot is broken enough to ask for a restart, and backs off when restarts do not help. It outlives the bot
/// instance - a restart creates a new <see cref="BotPlayer"/> for the same character - so a bot which breaks
/// itself again right after a restart is remembered as one which flaps, and quarantined instead of restarted in
/// a loop. One supervisor belongs to one character; <see cref="BotPlayer.Health"/> uses the character's, so two
/// bots of the same account don't share a quarantine or a restart history.
/// </summary>
/// <remarks>
/// The counters are what turns "a bot is broken" into something an operator can act on: the number of faults,
/// the kind of the last one, the number of restarts and - while a quarantine runs - until when the bot is out.
/// </remarks>
internal sealed class BotHealthSupervisor
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private int _consecutiveFaults;
    private int _restartStreak;
    private int _faults;
    private int _transientFaults;
    private int _ignoredTicks;
    private int _restarts;
    private string? _lastFault;
    private BotFaultKind? _lastFaultKind;
    private DateTimeOffset? _lastRestartUtc;
    private DateTimeOffset? _quarantinedUntilUtc;
    private string? _quarantineReason;
    private bool _quarantineReported;
    private DateTimeOffset? _lastEventUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotHealthSupervisor"/> class which uses the system clock.
    /// </summary>
    internal BotHealthSupervisor()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BotHealthSupervisor"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock for the quarantine and the forgiveness window.</param>
    internal BotHealthSupervisor(TimeProvider timeProvider)
    {
        this._timeProvider = timeProvider;
    }

    /// <summary>Gets how many AI ticks failed in a row; reset by every successful tick.</summary>
    internal int ConsecutiveFaults
    {
        get
        {
            lock (this._lock)
            {
                return this._consecutiveFaults;
            }
        }
    }

    /// <summary>Gets how many AI ticks failed so far, over all kinds except <see cref="BotFaultKind.Stopped"/>.</summary>
    internal int Faults
    {
        get
        {
            lock (this._lock)
            {
                return this._faults;
            }
        }
    }

    /// <summary>Gets how many of the faults were classified as transient.</summary>
    internal int TransientFaults
    {
        get
        {
            lock (this._lock)
            {
                return this._transientFaults;
            }
        }
    }

    /// <summary>Gets how many failed ticks were ignored because the bot was stopping anyway.</summary>
    internal int IgnoredTicks
    {
        get
        {
            lock (this._lock)
            {
                return this._ignoredTicks;
            }
        }
    }

    /// <summary>Gets how many times this account was restarted to heal it.</summary>
    internal int Restarts
    {
        get
        {
            lock (this._lock)
            {
                return this._restarts;
            }
        }
    }

    /// <summary>Gets the exception of the last failed tick, in short form.</summary>
    internal string? LastFault
    {
        get
        {
            lock (this._lock)
            {
                return this._lastFault;
            }
        }
    }

    /// <summary>Gets until when the bot is quarantined, or <c>null</c> while it may run and be restarted.</summary>
    internal DateTimeOffset? QuarantinedUntilUtc
    {
        get
        {
            lock (this._lock)
            {
                return this._quarantinedUntilUtc;
            }
        }
    }

    /// <summary>Gets how long the bot still has to live with its faults before a restart is tried again.</summary>
    /// <returns>The remaining quarantine, or <see cref="TimeSpan.Zero"/>.</returns>
    internal TimeSpan RemainingQuarantine()
    {
        lock (this._lock)
        {
            if (this._quarantinedUntilUtc is not { } until)
            {
                return TimeSpan.Zero;
            }

            var remaining = until - this._timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Records a failed AI tick.
    /// </summary>
    /// <param name="exception">The exception the tick threw, if the caller has one.</param>
    /// <returns><c>true</c> exactly at the tick which makes the bot ask for a restart.</returns>
    internal bool RecordFault(Exception? exception) => this.RecordFault(exception, out _);

    /// <summary>
    /// Records a failed AI tick and tells, under the same lock, whether it belongs in the log at error level.
    /// Deciding that in a second call would let a fault of the other timer change the count in between - a bot
    /// ticks on two timers, so the decision and the count have to go together.
    /// </summary>
    /// <param name="exception">The exception the tick threw, if the caller has one.</param>
    /// <param name="shouldLog"><c>true</c> if this failure belongs in the log at error level.</param>
    /// <returns><c>true</c> exactly at the tick which makes the bot ask for a restart.</returns>
    internal bool RecordFault(Exception? exception, out bool shouldLog)
    {
        var kind = BotHealthPolicy.Classify(exception);
        lock (this._lock)
        {
            this._lastEventUtc = this._timeProvider.GetUtcNow();
            if (kind == BotFaultKind.Stopped)
            {
                // A tick interrupted by the shutdown is expected, so it is neither counted nor logged - even
                // when no fault preceded it, where the quiet-log rule would otherwise ask for an error line.
                this._ignoredTicks++;
                shouldLog = false;
                return false;
            }

            // Computed before the count grows: the first FaultsLoggedAtError failures of a streak are the ones
            // logged at error level, and a fault which is only debugged must not move that boundary.
            shouldLog = BotHealthPolicy.ShouldLogFaultAtError(this._consecutiveFaults);

            this._faults++;
            if (kind == BotFaultKind.Transient)
            {
                this._transientFaults++;
            }

            this._lastFault = Describe(exception, kind);
            this._lastFaultKind = kind;

            // Deliberately '==', not '>=': the request is raised exactly once, at the tick which crosses the
            // threshold. Further faults keep counting but don't re-raise it; the maintenance pass consumes the
            // request and the restart resets the counter.
            return ++this._consecutiveFaults == BotHealthPolicy.ConsecutiveFaultsUntilRestart;
        }
    }

    /// <summary>
    /// Records a successful AI tick: the bot is not broken, so its consecutive faults are forgotten and - if it
    /// has been ticking for <see cref="BotHealthPolicy.StreakForgiveness"/> since its last restart - its restart
    /// streak, which ends a quarantine escalation.
    /// </summary>
    internal void RecordTickSucceeded()
    {
        lock (this._lock)
        {
            if (this._consecutiveFaults == 0 && this._restartStreak == 0)
            {
                return;
            }

            this._consecutiveFaults = 0;
            if (this._restartStreak > 0
                && this._lastRestartUtc is { } lastRestart
                && this._timeProvider.GetUtcNow() - lastRestart >= BotHealthPolicy.StreakForgiveness)
            {
                this._restartStreak = 0;
            }
        }
    }

    /// <summary>
    /// Asks whether the bot may be restarted now, and records the restart when it may. A quarantined bot keeps
    /// its restart request and is retried on a later pass, so nothing has to remember why it is out.
    /// </summary>
    /// <returns><c>true</c> if the caller may restart the bot now; <c>false</c> while it is quarantined.</returns>
    internal bool TryBeginRestart()
    {
        lock (this._lock)
        {
            var now = this._timeProvider.GetUtcNow();
            if (this._quarantinedUntilUtc is { } until && until > now)
            {
                return false;
            }

            this._lastEventUtc = now;
            this._restarts++;
            this._lastRestartUtc = now;
            this._restartStreak++;
            this._consecutiveFaults = 0;
            this._quarantinedUntilUtc = null;
            this._quarantineReason = null;
            this._quarantineReported = false;

            if (this._restartStreak >= BotHealthPolicy.RestartsUntilQuarantine)
            {
                // The restart did not stick. Keep the streak, so the next quarantine is longer - a bot which
                // breaks itself every few minutes must not load its account on that cadence.
                var quarantine = BotHealthPolicy.QuarantineFor(this._restartStreak);
                this._quarantinedUntilUtc = now + quarantine;
                this._quarantineReason = $"restarted {this._restartStreak}x without a healthy period; last fault: {this._lastFault}";
            }

            return true;
        }
    }

    /// <summary>
    /// Takes the quarantine report, if there is one which hasn't been reported yet.
    /// </summary>
    /// <returns>The line to log once, or <c>null</c> when there is nothing new to report.</returns>
    internal string? TakeQuarantineReport()
    {
        lock (this._lock)
        {
            if (this._quarantinedUntilUtc is not { } until || this._quarantineReported)
            {
                return null;
            }

            this._quarantineReported = true;
            return $"A bot was quarantined until {until:u}: {this._quarantineReason}.";
        }
    }

    /// <summary>
    /// Forgets everything an operator no longer wants held against the account: the quarantine, the restart streak
    /// and the counters. It does not restart the bot - a bot which still fails every tick keeps asking for one, and
    /// the next maintenance pass acts on that request as if the account were fresh. This is the lever for "I fixed
    /// the cause, let it run again" which otherwise only a server restart would provide.
    /// </summary>
    internal void Reset()
    {
        lock (this._lock)
        {
            this._consecutiveFaults = 0;
            this._restartStreak = 0;
            this._faults = 0;
            this._transientFaults = 0;
            this._ignoredTicks = 0;
            this._restarts = 0;
            this._lastFault = null;
            this._lastFaultKind = null;
            this._lastRestartUtc = null;
            this._quarantinedUntilUtc = null;
            this._quarantineReason = null;
            this._quarantineReported = false;
            this._lastEventUtc = null;
        }
    }

    /// <summary>
    /// Describes the health of the account in one line for the operator surfaces, or nothing while it is healthy.
    /// </summary>
    /// <returns>The line, or <c>null</c> when the account never faulted.</returns>
    internal string? Describe()
    {
        lock (this._lock)
        {
            if (this._faults == 0 && this._restarts == 0)
            {
                return null;
            }

            var parts = new List<string> { this._faults == 1 ? "1 faulted tick" : $"{this._faults} faulted ticks" };
            if (this._transientFaults > 0)
            {
                parts.Add(this._transientFaults == 1 ? "1 of them transient" : $"{this._transientFaults} of them transient");
            }

            if (this._lastFaultKind is { } kind && this._lastFault is { } fault)
            {
                parts.Add($"last was {BotHealthPolicy.Describe(kind)}: {fault}");
            }

            if (this._restarts > 0)
            {
                parts.Add($"restarted {this._restarts}x");
            }

            if (this._quarantinedUntilUtc is { } until)
            {
                parts.Add($"quarantined until {until:u}");
            }

            return "Health: " + string.Join(", ", parts) + ".";
        }
    }

    /// <summary>
    /// Determines whether this supervisor can be dropped from the per-account map: it has nothing to remember and
    /// nobody faulted for <see cref="BotHealthPolicy.ForgetAfter"/>.
    /// </summary>
    /// <returns><c>true</c> when the supervisor holds no state worth keeping.</returns>
    internal bool CanForget()
    {
        lock (this._lock)
        {
            var now = this._timeProvider.GetUtcNow();
            if (this._quarantinedUntilUtc is { } until && until > now)
            {
                return false;
            }

            return this._lastEventUtc is not { } lastEvent || now - lastEvent >= BotHealthPolicy.ForgetAfter;
        }
    }

    private static string Describe(Exception? exception, BotFaultKind kind)
        => exception is null
            ? "unknown (no exception reported)"
            : $"{exception.GetType().Name}: {FirstLine(exception.Message)} ({BotHealthPolicy.Describe(kind)})";

    private static string FirstLine(string message)
    {
        var lineBreak = message.IndexOfAny(['\r', '\n']);
        return lineBreak < 0 ? message : message[..lineBreak];
    }
}
