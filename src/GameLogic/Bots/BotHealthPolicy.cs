// <copyright file="BotHealthPolicy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.IO;

/// <summary>
/// What an exception thrown by a bot's AI tick says about the bot. The engine's attribute system is not
/// thread-safe, and a lost race can corrupt a character's attribute graph for good - from then on the same
/// exception comes out of every tick. That kind of fault is the one a fresh login cures; the others are
/// environment problems which a restart may not fix, so the log line and the counters say which one it is.
/// </summary>
internal enum BotFaultKind
{
    /// <summary>
    /// The tick was interrupted because the bot is being stopped or torn down. Not a fault: counting it would
    /// let a server shutdown look like a broken bot.
    /// </summary>
    Stopped,

    /// <summary>
    /// The tick failed on something outside the bot which may pass by itself - a timeout, a database which is
    /// briefly gone, an exhausted resource.
    /// </summary>
    Transient,

    /// <summary>
    /// The tick failed on the bot's own state: a null or disposed object, a cast or an index which does not fit,
    /// a key which is gone, a collection modified while it was walked. That is the shape a lost race takes, and
    /// the shape which does not heal by waiting.
    /// </summary>
    Corrupting,

    /// <summary>
    /// The tick failed on something which does not fit the other kinds. Counted like a corrupting fault, because
    /// a tick which always throws stops the bot just the same.
    /// </summary>
    Unexpected,
}

/// <summary>
/// The policy behind a <see cref="BotHealthSupervisor"/>: how a fault is classified, how patient the AI is
/// before it asks for a restart, and how the bot backs off when restarts do not help. The numbers live here so
/// the restart threshold and the backoff are one readable rule instead of constants spread over the bot and the
/// maintenance pass.
/// </summary>
internal static class BotHealthPolicy
{
    /// <summary>
    /// After this many AI ticks failing in a row, the bot counts as broken and asks for a restart. High enough
    /// that a single failing tick (a transient race, a monster which just died) is simply skipped, like before.
    /// </summary>
    internal const int ConsecutiveFaultsUntilRestart = 20;

    /// <summary>
    /// After this many restarts in a row, without a healthy period in between, the bot is quarantined instead of
    /// restarted right away - a bot which kills its own graph again within minutes must not flap through a fresh
    /// login every few minutes, because each one loads and saves the whole account.
    /// </summary>
    internal const int RestartsUntilQuarantine = 3;

    /// <summary>
    /// How many faulted bots one maintenance pass restarts. A corruption which hits many bots at once would
    /// otherwise restart all of them in the same second, and every restart loads an account and enters the world;
    /// the rest keep their request and are restarted on a later pass.
    /// </summary>
    internal const int RestartsPerMaintenancePass = 5;

    /// <summary>
    /// How many failed ticks of a streak are logged at error level before only every
    /// <see cref="FaultsPerQuietLog"/>th one is. A broken bot fails on two timers at once - the navigation tick every
    /// two seconds and the MU Helper tick every few hundred milliseconds - so keeping every failure in the log would
    /// bury the one line which matters.
    /// </summary>
    internal const int FaultsLoggedAtError = 3;

    /// <summary>
    /// After <see cref="FaultsLoggedAtError"/> failures of a streak, every this many failures is still logged at
    /// error level, so a long streak leaves a trace instead of going silent.
    /// </summary>
    internal const int FaultsPerQuietLog = 100;

    /// <summary>
    /// The quarantine after the first restart which did not stick; it doubles from there.
    /// </summary>
    internal static readonly TimeSpan BaseQuarantine = TimeSpan.FromMinutes(5);

    /// <summary>The longest quarantine - a bot isn't restarted for a while, but it is not written off either.</summary>
    internal static readonly TimeSpan MaximumQuarantine = TimeSpan.FromMinutes(60);

    /// <summary>
    /// How long a bot has to tick without faults to be forgiven its restarts. Shorter than a quarantine, so a
    /// bot which heals keeps a clean record.
    /// </summary>
    internal static readonly TimeSpan StreakForgiveness = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a supervisor without any fault or restart is worth keeping in memory. The map is keyed by account
    /// and would otherwise grow with every account which ever logged in - with the presence rotation that is
    /// every bot, all the time.
    /// </summary>
    internal static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(120);

    /// <summary>
    /// Classifies the exception of a failed AI tick.
    /// </summary>
    /// <param name="exception">The exception, if the caller has one.</param>
    /// <returns>The kind of fault.</returns>
    internal static BotFaultKind Classify(Exception? exception)
        => exception switch
        {
            null => BotFaultKind.Unexpected,
            OperationCanceledException or ObjectDisposedException => BotFaultKind.Stopped,
            TimeoutException or IOException or OutOfMemoryException => BotFaultKind.Transient,
            AggregateException { InnerExceptions.Count: > 0 } aggregate => Classify(aggregate.InnerExceptions[0]),
            NullReferenceException or InvalidCastException or InvalidOperationException
                or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentNullException
                or KeyNotFoundException => BotFaultKind.Corrupting,
            _ => BotFaultKind.Unexpected,
        };

    /// <summary>
    /// The quarantine which belongs to a restart streak: none until the streak reaches
    /// <see cref="RestartsUntilQuarantine"/>, then <see cref="BaseQuarantine"/> and doubling up to
    /// <see cref="MaximumQuarantine"/>.
    /// </summary>
    /// <param name="restartStreak">How many restarts happened in a row without a healthy period.</param>
    /// <returns>How long the bot has to wait before it is restarted again, or <see cref="TimeSpan.Zero"/> for none.</returns>
    internal static TimeSpan QuarantineFor(int restartStreak)
    {
        var steps = restartStreak - RestartsUntilQuarantine;
        if (steps < 0)
        {
            return TimeSpan.Zero;
        }

        var minutes = BaseQuarantine.TotalMinutes * Math.Pow(2, Math.Min(steps, 8));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaximumQuarantine.TotalMinutes));
    }

    /// <summary>
    /// Determines whether a failure belongs in the log at error level, given how many ticks already failed in a row.
    /// </summary>
    /// <param name="consecutiveFaults">How many ticks of the current streak already failed.</param>
    /// <returns><c>true</c> if the failure should be logged at error level, <c>false</c> to only debug it.</returns>
    internal static bool ShouldLogFaultAtError(int consecutiveFaults)
        => consecutiveFaults < FaultsLoggedAtError || consecutiveFaults % FaultsPerQuietLog == 0;

    /// <summary>
    /// Describes a fault kind for a log line or the operator surfaces.
    /// </summary>
    /// <param name="kind">The kind of fault.</param>
    /// <returns>The kind as a word, e.g. <c>corrupting</c>.</returns>
    internal static string Describe(BotFaultKind kind)
        => kind switch
        {
            BotFaultKind.Stopped => "stopped",
            BotFaultKind.Transient => "transient",
            BotFaultKind.Corrupting => "corrupting",
            _ => "unexpected",
        };
}
