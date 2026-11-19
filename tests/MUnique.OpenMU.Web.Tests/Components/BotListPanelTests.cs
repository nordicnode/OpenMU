// <copyright file="BotListPanelTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Components;

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Web.AdminPanel.Components.Bots;
using MUnique.OpenMU.Web.Shared.Services;

/// <summary>
/// Tests the bot table of the "Logged In" page: what it says about each bot, and that the trouble of an account -
/// how often its bot was restarted and whether it is held back right now - is visible in its row. The panel is its
/// own component, so it can be rendered without an in-process game server, which the page's tab needs.
/// </summary>
[TestFixture]
public class BotListPanelTests
{
    /// <summary>
    /// A bot which keeps failing shows what was done about it, in the row the operator already reads.
    /// </summary>
    [Test]
    public void ShowsTheRestartsOfABot()
    {
        using var context = CreateContext(
            Bot("bot-flap", "Kain", restarts: 4),
            Bot("bot-fine", "Lena"));

        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        var flapping = RowOf(panel, "bot-flap");
        var fine = RowOf(panel, "bot-fine");

        Assert.That(flapping.TextContent, Does.Contain("4× restarts"));
        Assert.That(fine.TextContent, Does.Not.Contain("restarts"));
    }

    /// <summary>
    /// A bot which is held back from another restart right now is the more urgent case, so it says that instead of
    /// the restart count - and it is marked as a warning, not as a fact.
    /// </summary>
    [Test]
    public void ShowsTheQuarantineOfABot()
    {
        using var context = CreateContext(Bot("bot-held", "Mo", restarts: 3, quarantinedSeconds: 150));

        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        var badge = RowOf(panel, "bot-held").QuerySelector(".badge");
        Assert.That(badge, Is.Not.Null);
        Assert.That(badge!.TextContent, Is.EqualTo("quarantined 3 min"));
        Assert.That(badge.ClassList, Does.Contain("bg-warning"));
    }

    /// <summary>
    /// A bot which announced an event meeting says which state it is in, where the meeting is and how long it
    /// still waits - in the row the operator already reads, without opening a log or asking the bot.
    /// </summary>
    [Test]
    public void ShowsTheEventMeetingOfABot()
    {
        using var context = CreateContext(
            Bot(
                "bot-meet",
                "Kai",
                meeting: new BotMeetingActivity(
                    "Waiting",
                    "Devias 215, 40",
                    "Blood Castle",
                    DateTime.UtcNow.AddMinutes(7).AddSeconds(48))),
            Bot("bot-fine", "Lena"));

        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        var meeting = RowOf(panel, "bot-meet");
        Assert.That(meeting.TextContent, Does.Match("Waiting at Devias 215, 40, [0-9]+:[0-9]{2} left"));
        Assert.That(RowOf(panel, "bot-fine").TextContent, Does.Not.Contain("left"));
    }

    /// <summary>
    /// The countdown of a meeting keeps moving while the panel is open instead of freezing at the value read
    /// with the page - the panel re-renders it every second from the meeting's end time.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task CountsTheMeetingDownWhileThePanelIsOpenAsync()
    {
        using var context = CreateContext(
            Bot(
                "bot-meet",
                "Kai",
                meeting: new BotMeetingActivity("Waiting", "Devias 215, 40", null, DateTime.UtcNow.AddMinutes(1))));
        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        var before = RowOf(panel, "bot-meet").TextContent;
        Assert.That(before, Does.Match("Waiting at Devias 215, 40, [0-9]+:[0-9]{2} left"));

        // Nothing renders the panel here: the row has to change because the panel re-renders itself.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (panel.RenderCount == 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.That(panel.RenderCount, Is.GreaterThan(1), "the open panel re-renders itself");
        Assert.That(RowOf(panel, "bot-meet").TextContent, Is.Not.EqualTo(before));
    }

    /// <summary>
    /// A meeting which is over still explains itself in the row: what became of it and where, with the reason
    /// in the tooltip - and a new, active meeting takes the line over.
    /// </summary>
    [Test]
    public void ShowsTheOutcomeOfAnEndedMeeting()
    {
        var waiting = new BotMeetingActivity(
            "Waiting",
            "Devias 215, 40",
            "Blood Castle",
            DateTime.UtcNow.AddMinutes(7).AddSeconds(48));
        using var context = CreateContext(
            Bot(
                "bot-lost",
                "Kai",
                lastMeeting: new BotMeetingOutcome("Abandoned", "Lost Tower 200, 200", "Blood Castle", "no route to the entry NPC")),
            Bot(
                "bot-meet",
                "Lena",
                meeting: waiting,
                lastMeeting: new BotMeetingOutcome("Expired", "Devias 215, 40", null, null)));

        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        var lost = RowOf(panel, "bot-lost");
        Assert.That(lost.TextContent, Does.Contain("Last meeting: abandoned at Lost Tower 200, 200"));
        Assert.That(lost.QuerySelector(".text-muted")!.GetAttribute("title"), Does.Contain("no route to the entry NPC"));

        var meeting = RowOf(panel, "bot-meet");
        Assert.That(meeting.TextContent, Does.Match("Waiting at Devias 215, 40, [0-9]+:[0-9]{2} left"));
        Assert.That(meeting.TextContent, Does.Not.Contain("Last meeting:"));
    }

    /// <summary>
    /// The alarm about the bots which need attention is shown above the table, where the operator looks before
    /// reading any row.
    /// </summary>
    [Test]
    public void ShowsTheAlarmAboveTheTable()
    {
        using var context = CreateContext(Bot("bot-flap", "Kain", restarts: 4));

        var panel = context.Render<BotListPanel>(parameters => parameters
            .Add(p => p.AlarmLine, "1 of 1 bots keeps failing: bot-flap (4 restarts)."));

        Assert.That(panel.Find("#bots-stuck-alarm").TextContent, Does.Contain("4 restarts"));
    }

    /// <summary>
    /// The panel only offers the reset where there is something to forget, and it hands the row to the page - which
    /// decides whether the operator may do it and what it reports.
    /// </summary>
    [Test]
    public void OffersTheResetOnlyWhereThereIsSomethingToForget()
    {
        using var context = CreateContext(
            Bot("bot-flap", "Kain", restarts: 4),
            Bot("bot-held", "Mo", restarts: 3, quarantinedSeconds: 150),
            Bot("bot-fine", "Lena"));

        BotAccount? reset = null;
        var panel = context.Render<BotListPanel>(parameters => parameters
            .Add(p => p.AlarmLine, (string?)null)
            .Add(p => p.OnResetHealth, account => reset = account));

        var resetButtons = panel.FindAll("button.admin-table-action");
        Assert.That(resetButtons, Has.Count.EqualTo(2));

        resetButtons[0].Click();

        Assert.That(reset, Is.Not.Null);
        Assert.That(reset!.LoginName, Is.EqualTo("bot-flap"));
    }

    /// <summary>
    /// A healthy server renders an empty table without an alarm and without a health badge - the column still
    /// exists, so the table doesn't change its shape when a bot starts failing.
    /// </summary>
    [Test]
    public void KeepsItsShapeWhileEveryBotIsHealthy()
    {
        using var context = CreateContext(Bot("bot-fine", "Lena"));

        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        Assert.That(panel.FindAll("#bots-stuck-alarm"), Is.Empty);
        Assert.That(panel.FindAll("thead th"), Has.Count.EqualTo(9));
        Assert.That(panel.FindAll(".badge"), Is.Empty);
        Assert.That(panel.FindAll("button.admin-table-action"), Is.Empty);
    }

    private static BunitContext CreateContext(params BotAccount[] bots)
    {
        var context = new BunitContext();
        context.Services.AddSingleton<IDataService<BotAccount>>(new FixedBotService(bots));
        return context;
    }

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<BotListPanel> panel, string loginName)
        => panel.FindAll("tbody tr").Single(row => row.QuerySelector("td")!.TextContent.Trim() == loginName);

    private static BotAccount Bot(string loginName, string characterName, int restarts = 0, double quarantinedSeconds = 0, BotMeetingActivity? meeting = null, BotMeetingOutcome? lastMeeting = null)
        => new(loginName, 1, characterName, new DateTime(2026, 2, 11, 9, 12, 4, DateTimeKind.Utc), State: "Hunt for 0:30 (monsters nearby)", SecondsInState: 30, Restarts: restarts, QuarantinedSeconds: quarantinedSeconds, Meeting: meeting, LastMeeting: lastMeeting);

    private sealed class FixedBotService(params BotAccount[] bots) : IDataService<BotAccount>
    {
        public Task<List<BotAccount>> GetAsync(int offset, int count) => Task.FromResult(bots.Skip(offset).Take(count).ToList());
    }
}
