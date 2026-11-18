// <copyright file="BotStuckAlarmTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Operations;

using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>
/// Tests the rule which turns the bots' modes and durations into the alarm an operator needs: which bots are
/// stuck, how many, and in which mode.
/// </summary>
[TestFixture]
public class BotStuckAlarmTest
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A bot waiting in one of the modes it leaves quickly is what the alarm is for; a bot which is busy hunting
    /// is not stuck, however long it has been at it.
    /// </summary>
    [Test]
    public void FindsTheWaitingBotsAndIgnoresTheBusyOnes()
    {
        var modes = BotStuckAlarm.Find(
            [
                ("Idle", 11 * 60d),
                ("Shopping", 12 * 60d),
                ("Hunt", 60 * 60d),
                ("FollowLeader", 60 * 60d),
                ("MarketShift", 90 * 60d),
            ],
            TenMinutes);

        Assert.That(modes.Select(mode => mode.State), Is.EquivalentTo(new[] { "Idle", "Shopping" }));
    }

    /// <summary>
    /// The threshold is inclusive: a bot which waits exactly that long is reported, one second less is not.
    /// </summary>
    [Test]
    public void CountsTheWaitFromTheThresholdOn()
    {
        var onTheThreshold = BotStuckAlarm.Find([("Idle", TenMinutes.TotalSeconds)], TenMinutes);
        var justBelow = BotStuckAlarm.Find([("Idle", TenMinutes.TotalSeconds - 1)], TenMinutes);

        Assert.That(onTheThreshold, Has.Count.EqualTo(1));
        Assert.That(justBelow, Is.Empty);
    }

    /// <summary>
    /// Bots which wait in the same mode are one entry, because "twelve bots idle" is the finding, not twelve.
    /// </summary>
    [Test]
    public void GroupsTheBotsOfAModeTogether()
    {
        var modes = BotStuckAlarm.Find(
            [("Idle", 11 * 60d), ("Idle", 30 * 60d), ("Idle", 45 * 60d)],
            TenMinutes);

        Assert.That(modes, Has.Count.EqualTo(1));
        Assert.That(modes[0].Count, Is.EqualTo(3));
        Assert.That(modes[0].LongestSeconds, Is.EqualTo(45 * 60d), "the longest wait is what an operator cares about");
    }

    /// <summary>
    /// The mode which holds the most bots comes first, so the first line of a report is the largest problem.
    /// </summary>
    [Test]
    public void PutsTheModeWithTheMostBotsFirst()
    {
        var modes = BotStuckAlarm.Find(
            [("Shopping", 60 * 60d), ("Idle", 11 * 60d), ("Idle", 12 * 60d)],
            TenMinutes);

        Assert.That(modes.Select(mode => mode.State), Is.EqualTo(new[] { "Idle", "Shopping" }));
    }

    /// <summary>
    /// The sentence says how many of how many bots are stuck and in which modes, so it can be appended to a
    /// summary or shown as a banner without further context.
    /// </summary>
    [Test]
    public void DescribesTheAlarmInOneSentence()
    {
        var modes = BotStuckAlarm.Find(
            [("Idle", 11 * 60d), ("Idle", 12 * 60d), ("Shopping", 13 * 60d), ("Hunt", 60 * 60d)],
            TenMinutes);

        var line = BotStuckAlarm.Describe(modes, 20, TenMinutes);

        Assert.That(line, Is.EqualTo("3 of 20 bots wait for more than 10 minutes: Idle 2, Shopping 1."));
    }

    /// <summary>
    /// A healthy server gets no sentence at all, so the surfaces can show the alarm only when there is one.
    /// </summary>
    [Test]
    public void SaysNothingWhileNoBotIsStuck()
    {
        var modes = BotStuckAlarm.Find([("Hunt", 60 * 60d)], TenMinutes);

        Assert.That(BotStuckAlarm.Describe(modes, 12, TenMinutes), Is.Empty);
    }
}
