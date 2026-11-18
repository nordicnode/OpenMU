// <copyright file="BotHealthPolicyTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests the failure policy of the bots: which exceptions count as the engine corruption a restart cures, which
/// ones are environmental, and how a bot which keeps breaking itself backs off.
/// </summary>
[TestFixture]
public class BotHealthPolicyTest
{
    /// <summary>
    /// A bot which is being stopped is not a broken bot - a shutdown would otherwise look like a wave of failures.
    /// </summary>
    [Test]
    public void CancellationAndTeardownAreNoFaults()
    {
        Assert.That(BotHealthPolicy.Classify(new OperationCanceledException()), Is.EqualTo(BotFaultKind.Stopped));
        Assert.That(BotHealthPolicy.Classify(new ObjectDisposedException("bot")), Is.EqualTo(BotFaultKind.Stopped));
    }

    /// <summary>
    /// The exceptions a corrupted attribute graph throws are recognized as the kind a fresh login cures.
    /// </summary>
    [TestCase(typeof(NullReferenceException))]
    [TestCase(typeof(InvalidCastException))]
    [TestCase(typeof(InvalidOperationException))]
    [TestCase(typeof(IndexOutOfRangeException))]
    [TestCase(typeof(ArgumentOutOfRangeException))]
    [TestCase(typeof(ArgumentNullException))]
    [TestCase(typeof(KeyNotFoundException))]
    public void ObjectGraphDamagesAreCorrupting(Type exceptionType)
    {
        Assert.That(BotHealthPolicy.Classify(Create(exceptionType)), Is.EqualTo(BotFaultKind.Corrupting));
    }

    /// <summary>
    /// A database which is briefly gone or a timeout outlives no restart, so it is told apart.
    /// </summary>
    [Test]
    public void EnvironmentProblemsAreTransient()
    {
        Assert.That(BotHealthPolicy.Classify(new TimeoutException()), Is.EqualTo(BotFaultKind.Transient));
        Assert.That(BotHealthPolicy.Classify(new IOException()), Is.EqualTo(BotFaultKind.Transient));
    }

    /// <summary>
    /// Everything else is counted like a corrupting fault - a tick which keeps throwing stops the bot anyway - but
    /// reported as unknown rather than as damage.
    /// </summary>
    [Test]
    public void UnknownExceptionsAreUnexpected()
    {
        Assert.That(BotHealthPolicy.Classify(new NotSupportedException()), Is.EqualTo(BotFaultKind.Unexpected));
        Assert.That(BotHealthPolicy.Classify(null), Is.EqualTo(BotFaultKind.Unexpected));
    }

    /// <summary>
    /// The engine's own aggregation wrappers - a tick's exception may arrive wrapped - are looked through.
    /// </summary>
    [Test]
    public void AggregateExceptionsAreClassifiedByTheirInner()
    {
        var aggregate = new AggregateException(new ArgumentException(), new NullReferenceException());
        Assert.That(BotHealthPolicy.Classify(aggregate), Is.EqualTo(BotFaultKind.Unexpected));
        Assert.That(
            BotHealthPolicy.Classify(new AggregateException(new NullReferenceException())),
            Is.EqualTo(BotFaultKind.Corrupting));
    }

    /// <summary>
    /// The first restarts are tries, not flapping - each quarantine after the third restart lasts longer, up to
    /// the cap, so a bot which breaks itself every few minutes stops loading its account that often.
    /// </summary>
    [Test]
    public void QuarantinesGrowWithTheRestartStreak()
    {
        Assert.That(BotHealthPolicy.QuarantineFor(1), Is.EqualTo(TimeSpan.Zero));
        Assert.That(BotHealthPolicy.QuarantineFor(BotHealthPolicy.RestartsUntilQuarantine - 1), Is.EqualTo(TimeSpan.Zero));
        Assert.That(
            BotHealthPolicy.QuarantineFor(BotHealthPolicy.RestartsUntilQuarantine),
            Is.EqualTo(BotHealthPolicy.BaseQuarantine));
        Assert.That(
            BotHealthPolicy.QuarantineFor(BotHealthPolicy.RestartsUntilQuarantine + 1),
            Is.EqualTo(BotHealthPolicy.BaseQuarantine * 2));
        Assert.That(
            BotHealthPolicy.QuarantineFor(BotHealthPolicy.RestartsUntilQuarantine + 2),
            Is.EqualTo(BotHealthPolicy.BaseQuarantine * 4));
    }

    /// <summary>
    /// The quarantine never grows past its cap - the bot comes back eventually instead of dropping out for good.
    /// </summary>
    [Test]
    public void QuarantineIsCapped()
    {
        Assert.That(BotHealthPolicy.QuarantineFor(1000), Is.EqualTo(BotHealthPolicy.MaximumQuarantine));
    }

    /// <summary>
    /// A broken bot fails on two timers at once, so only the start of a streak and a trace every hundred failures
    /// reach the log at error level - the flood would bury the diagnosis.
    /// </summary>
    [Test]
    public void OnlyTheStartOfAFailureStreakIsLoggedLoudly()
    {
        for (var alreadyFailed = 0; alreadyFailed < BotHealthPolicy.FaultsLoggedAtError; alreadyFailed++)
        {
            Assert.That(BotHealthPolicy.ShouldLogFaultAtError(alreadyFailed), Is.True, $"failure #{alreadyFailed + 1}");
        }

        Assert.That(BotHealthPolicy.ShouldLogFaultAtError(BotHealthPolicy.FaultsLoggedAtError), Is.False);
        Assert.That(BotHealthPolicy.ShouldLogFaultAtError(BotHealthPolicy.FaultsLoggedAtError + 1), Is.False);
        Assert.That(BotHealthPolicy.ShouldLogFaultAtError(BotHealthPolicy.FaultsPerQuietLog), Is.True);
        Assert.That(BotHealthPolicy.ShouldLogFaultAtError(BotHealthPolicy.FaultsPerQuietLog + 1), Is.False);
        Assert.That(BotHealthPolicy.ShouldLogFaultAtError(BotHealthPolicy.FaultsPerQuietLog * 2), Is.True);
    }

    private static Exception Create(Type exceptionType)
        => (Exception)Activator.CreateInstance(exceptionType)!;
}
