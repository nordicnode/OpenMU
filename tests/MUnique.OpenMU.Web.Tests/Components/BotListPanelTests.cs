// <copyright file="BotListPanelTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Components;

using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
    /// A healthy server renders an empty table without an alarm and without a health badge - the column still
    /// exists, so the table doesn't change its shape when a bot starts failing.
    /// </summary>
    [Test]
    public void KeepsItsShapeWhileEveryBotIsHealthy()
    {
        using var context = CreateContext(Bot("bot-fine", "Lena"));

        var panel = context.Render<BotListPanel>(parameters => parameters.Add(p => p.AlarmLine, (string?)null));

        Assert.That(panel.FindAll("#bots-stuck-alarm"), Is.Empty);
        Assert.That(panel.FindAll("thead th"), Has.Count.EqualTo(8));
        Assert.That(panel.FindAll(".badge"), Is.Empty);
    }

    private static BunitContext CreateContext(params BotAccount[] bots)
    {
        var context = new BunitContext();
        context.Services.AddSingleton<IDataService<BotAccount>>(new FixedBotService(bots));
        return context;
    }

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<BotListPanel> panel, string loginName)
        => panel.FindAll("tbody tr").Single(row => row.QuerySelector("td")!.TextContent.Trim() == loginName);

    private static BotAccount Bot(string loginName, string characterName, int restarts = 0, double quarantinedSeconds = 0)
        => new(loginName, 1, characterName, new DateTime(2026, 2, 11, 9, 12, 4, DateTimeKind.Utc), State: "Hunt for 0:30 (monsters nearby)", SecondsInState: 30, Restarts: restarts, QuarantinedSeconds: quarantinedSeconds);

    private sealed class FixedBotService(params BotAccount[] bots) : IDataService<BotAccount>
    {
        public Task<List<BotAccount>> GetAsync(int offset, int count) => Task.FromResult(bots.Skip(offset).Take(count).ToList());
    }
}
