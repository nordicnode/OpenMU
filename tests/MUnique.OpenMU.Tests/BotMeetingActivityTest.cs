// <copyright file="BotMeetingActivityTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests what an operator reads about a bot's event meeting: the state it is in, the place it goes to or
/// waits at, and the time it still has for it.
/// </summary>
[TestFixture]
public class BotMeetingActivityTest
{
    private static readonly DateTime Now = new(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A bot without a meeting has nothing to report, so the administration surfaces leave the cell empty
    /// instead of inventing a place or a countdown.
    /// </summary>
    [Test]
    public void NoMeetingIsNotReported()
        => Assert.That(BotMeetingActivity.From(null), Is.Null);

    /// <summary>
    /// A bot which arrived reports that it waits, where, and how long it still waits there - the two facts a
    /// player who read the announcement needs to decide whether to follow it.
    /// </summary>
    [Test]
    public void ReportsWhereAndHowLongABotWaits()
    {
        var meeting = new BotEventMeeting(
            new GameMapDefinition { Number = 2, Name = "Devias" },
            new Point(215, 40),
            Now.AddSeconds(468));
        meeting.AdvanceTo(BotMeetingState.Waiting);

        var activity = BotMeetingActivity.From(meeting);

        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.State, Is.EqualTo(nameof(BotMeetingState.Waiting)));
        Assert.That(activity.Place, Is.EqualTo("Devias 215, 40"));
        Assert.That(activity.Event, Is.Null);
        Assert.That(activity.RemainingAt(Now), Is.EqualTo(TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(48)));
        Assert.That(activity.SummaryAt(Now), Is.EqualTo("Waiting at Devias 215, 40, 7:48 left"));
        Assert.That(activity.DetailAt(Now), Does.Contain("meeting waiting at"));
        Assert.That(activity.DetailAt(Now), Does.Contain("at Devias 215, 40"));
        Assert.That(activity.DetailAt(Now), Does.Contain("7:48 left"));
    }

    /// <summary>
    /// The countdown is derived from the absolute end of the meeting, so a surface which re-renders moves it -
    /// that is what lets the bot table tick without reloading its rows.
    /// </summary>
    [Test]
    public void CountsTheRemainingWaitTimeDown()
    {
        var activity = new BotMeetingActivity("Waiting", "Devias 215, 40", null, Now.AddSeconds(468));

        Assert.That(activity.SummaryAt(Now), Is.EqualTo("Waiting at Devias 215, 40, 7:48 left"));
        Assert.That(activity.SummaryAt(Now.AddSeconds(48)), Is.EqualTo("Waiting at Devias 215, 40, 7:00 left"));
        Assert.That(activity.SummaryAt(Now.AddMinutes(10)), Is.EqualTo("Waiting at Devias 215, 40, 0:00 left"));
    }

    /// <summary>
    /// A bot which is still on its way says so, and names the event whose entrance it belongs to, so an
    /// operator can tell the wait before the event from the wait after the meeting time ran out.
    /// </summary>
    [Test]
    public void ReportsTheTravelToAnEventMeeting()
    {
        var meeting = new BotEventMeeting(
            new GameMapDefinition { Number = 4, Name = "Lost Tower" },
            new Point(200, 200),
            Now.AddMinutes(5),
            Event: new MiniGameDefinition { Name = "Blood Castle" });
        meeting.AdvanceTo(BotMeetingState.Traveling);

        var activity = BotMeetingActivity.From(meeting);

        Assert.That(activity!.State, Is.EqualTo(nameof(BotMeetingState.Traveling)));
        Assert.That(activity.Place, Is.EqualTo("Lost Tower 200, 200"));
        Assert.That(activity.Event, Is.EqualTo("Blood Castle"));
        Assert.That(activity.SummaryAt(Now), Is.EqualTo("Traveling to Lost Tower 200, 200, 5:00 left"));
        Assert.That(activity.DetailAt(Now), Does.Contain("(Blood Castle)"));
    }

    /// <summary>
    /// A fallback rendezvous has no entry NPC point: the bot waits in the safezone of the map, and the place
    /// says that instead of pretending to know coordinates.
    /// </summary>
    [Test]
    public void ReportsASafezoneRendezvous()
    {
        var meeting = new BotEventMeeting(new GameMapDefinition { Number = 3, Name = "Devias" }, null, Now.AddMinutes(5));
        meeting.AdvanceTo(BotMeetingState.Waiting);

        var activity = BotMeetingActivity.From(meeting);

        Assert.That(activity!.Place, Is.EqualTo("Devias (safezone)"));
    }

    /// <summary>
    /// A custom map without a name is reported by its number, so the place is never an empty cell; the
    /// timestamps of a meeting which already ran out never turn into a negative countdown.
    /// </summary>
    [Test]
    public void ReportsANamelessMapAndATimedOutMeeting()
    {
        var meeting = new BotEventMeeting(new GameMapDefinition { Number = 77 }, null, Now.AddMinutes(-1));
        meeting.AdvanceTo(BotMeetingState.Expired);

        var activity = BotMeetingActivity.From(meeting);

        Assert.That(activity!.Place, Is.EqualTo("map 77 (safezone)"));
        Assert.That(activity.RemainingAt(Now), Is.EqualTo(TimeSpan.Zero));
        Assert.That(activity.SummaryAt(Now), Is.EqualTo("Meeting at map 77 (safezone), 0:00 left"));
    }
}
