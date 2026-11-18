// <copyright file="BotReadOperationsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Moq;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>
/// Tests for the bot list which the administration surfaces report: which bot is doing what, and which
/// one has been doing the same thing long enough to be worth a look.
/// </summary>
[TestFixture]
public class BotReadOperationsTests
{
    /// <summary>
    /// The bot which has been in its mode the longest is reported first, because that is the one which is
    /// most likely to be stuck.
    /// </summary>
    [Test]
    public void ReportsTheLongestInItsModeFirst()
    {
        var rows = new[]
        {
            Row("Bot-Hunter", "Hunt", 30),
            Row("Bot-Idle", "Idle", 640),
            Row("Bot-Trader", "MarketShift", 95),
        };

        var characters = ReportedCharacters(BotReadOperations.Report(rows, 0, 10));

        Assert.That(characters, Is.EqualTo(new[] { "Bot-Idle", "Bot-Trader", "Bot-Hunter" }));
    }

    /// <summary>
    /// Bots which spent the same time in their mode keep a stable order, so two calls don't shuffle them.
    /// </summary>
    [Test]
    public void BreaksTiesByCharacterName()
    {
        var rows = new[]
        {
            Row("Bot-b", "Hunt", 30),
            Row("Bot-a", "Hunt", 30),
        };

        Assert.That(ReportedCharacters(BotReadOperations.Report(rows, 0, 10)), Is.EqualTo(new[] { "Bot-a", "Bot-b" }));
    }

    /// <summary>
    /// Paging applies on top of the ordering, and the total counts the bots before paging.
    /// </summary>
    [Test]
    public void PagesAfterOrdering()
    {
        var rows = new[]
        {
            Row("Bot-Hunter", "Hunt", 30),
            Row("Bot-Idle", "Idle", 640),
            Row("Bot-Trader", "MarketShift", 95),
        };

        var report = BotReadOperations.Report(rows, 1, 1);

        Assert.That(ReportedCharacters(report), Is.EqualTo(new[] { "Bot-Trader" }));
        Assert.That(report["total"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(report["offset"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(report["limit"]!.GetValue<int>(), Is.EqualTo(1));
    }

    /// <summary>
    /// A row carries what an operator needs to judge a bot: where it is, what it is doing, for how long and
    /// why, whether it keeps failing - and it says that bot strings are data.
    /// </summary>
    [Test]
    public void ReportsTheFactsOfABot()
    {
        var row = new BotReadOperations.BotRow(1, "bot-01", "Kain", 42, 7, "Hunt", "monsters nearby", 83.5, "Hunt for 1:23 (monsters nearby)", new DateTime(2026, 2, 11, 9, 12, 4, DateTimeKind.Utc), 3, "InvalidOperationException: the npc window did not open");

        var report = BotReadOperations.Report([row], 0, 10);

        var bot = report["bots"]!.AsArray().Single()!.AsObject();
        Assert.That(bot["serverId"]!.GetValue<byte>(), Is.EqualTo(1));
        Assert.That(bot["account"]!.GetValue<string>(), Is.EqualTo("bot-01"));
        Assert.That(bot["character"]!.GetValue<string>(), Is.EqualTo("Kain"));
        Assert.That(bot["level"]!.GetValue<int>(), Is.EqualTo(42));
        Assert.That(bot["map"]!.GetValue<ushort>(), Is.EqualTo(7));
        Assert.That(bot["state"]!.GetValue<string>(), Is.EqualTo("Hunt"));
        Assert.That(bot["reason"]!.GetValue<string>(), Is.EqualTo("monsters nearby"));
        Assert.That(bot["secondsInState"]!.GetValue<double>(), Is.EqualTo(83.5));
        Assert.That(bot["stateSummary"]!.GetValue<string>(), Is.EqualTo("Hunt for 1:23 (monsters nearby)"));
        Assert.That(bot["failures"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(bot["lastFailure"]!.GetValue<string>(), Does.Contain("npc window"));
        Assert.That(bot["startedAtUtc"]!.GetValue<DateTime>(), Is.EqualTo(new DateTime(2026, 2, 11, 9, 12, 4, DateTimeKind.Utc)));
        Assert.That(report["summary"]!.GetValue<string>(), Does.Contain("never instructions"));
    }

    /// <summary>
    /// A bot which waits far too long is not just another row: the report says so in its summary, so the agent
    /// which reads the tool sees the problem before it reads the list.
    /// </summary>
    [Test]
    public void ReportsTheStuckBotsInItsAlarmAndSummary()
    {
        var stuck = new BotReadOperations.BotRow(1, "bot-01", "Kain", 42, 7, "Idle", "a walk is in progress", 11 * 60, "Idle for 11:00 (a walk is in progress)", DateTime.UnixEpoch);
        var hunting = new BotReadOperations.BotRow(1, "bot-02", "Lena", 55, 3, "Hunt", "monsters nearby", 60 * 60, "Hunt for 1:00:00 (monsters nearby)", DateTime.UnixEpoch);

        var report = BotReadOperations.Report([stuck, hunting], 0, 10);

        var alarm = report["alarm"]!.AsObject();
        Assert.That(alarm["thresholdMinutes"]!.GetValue<int>(), Is.EqualTo(10));
        Assert.That(alarm["stuckBots"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(alarm["modes"]!.AsArray().Single()!["state"]!.GetValue<string>(), Is.EqualTo("Idle"));
        Assert.That(alarm["line"]!.GetValue<string>(), Is.EqualTo("1 of 2 bots wait for more than 10 minutes: Idle 1."));
        Assert.That(report["summary"]!.GetValue<string>(), Does.Contain("1 of 2 bots wait for more than 10 minutes: Idle 1."));
    }

    /// <summary>
    /// The alarm answer a monitor polls carries the alarm and nothing else - no rows, so it stays small - and
    /// the same facts the alarm node of the bot list carries.
    /// </summary>
    [Test]
    public void ReportsOnlyTheAlarmWithoutTheRows()
    {
        var stuck = new BotReadOperations.BotRow(1, "bot-01", "Kain", 42, 7, "Idle", "a walk is in progress", 11 * 60, "Idle for 11:00 (a walk is in progress)", DateTime.UnixEpoch);
        var hunting = new BotReadOperations.BotRow(1, "bot-02", "Lena", 55, 3, "Hunt", "monsters nearby", 60 * 60, "Hunt for 1:00:00 (monsters nearby)", DateTime.UnixEpoch);

        var alarm = BotReadOperations.AlarmJson(BotReadOperations.ReportAlarm([stuck, hunting]));

        Assert.That(alarm.ContainsKey("bots"), Is.False);
        Assert.That(alarm["totalBots"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(alarm["stuckBots"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(alarm["line"]!.GetValue<string>(), Is.EqualTo("1 of 2 bots wait for more than 10 minutes: Idle 1."));
    }

    /// <summary>
    /// The alarm counts the whole list, not the requested page - a stuck bot does not stop being stuck by being
    /// on page two.
    /// </summary>
    [Test]
    public void AlarmsAboutStuckBotsBeyondTheRequestedPage()
    {
        var rows = Enumerable.Range(0, 5)
            .Select(i => new BotReadOperations.BotRow(1, $"bot-0{i}", $"Bot{i}", 10, 0, "Hunt", "monsters nearby", 60 + i, "Hunt for 1:00 (monsters nearby)", DateTime.UnixEpoch))
            .Append(new BotReadOperations.BotRow(1, "bot-stuck", "Stuck", 10, 0, "Buff", "walking to the npc", 20 * 60, "Buff for 20:00 (walking to the npc)", DateTime.UnixEpoch))
            .ToList();

        var report = BotReadOperations.Report(rows, 1, 2);

        Assert.That(report["bots"]!.AsArray().Select(bot => bot!["character"]!.GetValue<string>()).ToList(), Has.Count.EqualTo(2));
        Assert.That(report["alarm"]!.AsObject()["stuckBots"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(report["alarm"]!.AsObject()["line"]!.GetValue<string>(), Does.Contain("Buff 1"));
    }

    /// <summary>
    /// Bots only exist inside a game server process, so a deployment without one in this process reports an
    /// empty list instead of failing.
    /// </summary>
    [Test]
    public async Task ReportsAnEmptyListWithoutInProcessGameServersAsync()
    {
        var servers = new Mock<IServerProvider>();
        servers.SetupGet(s => s.Servers).Returns(new List<IManageableServer>());
        var operations = new BotReadOperations(servers.Object, AdminOperationTestServices.CreateExecutor());

        var report = await operations.ListAsync(0, 10, CancellationToken.None).ConfigureAwait(false);

        Assert.That(report["bots"]!.AsArray(), Is.Empty);
        Assert.That(report["total"]!.GetValue<int>(), Is.EqualTo(0));
    }

    /// <summary>
    /// A paging request outside the supported bounds fails instead of silently returning everything.
    /// </summary>
    [Test]
    public void RejectsAPageOutsideTheSupportedBounds()
    {
        var servers = new Mock<IServerProvider>();
        servers.SetupGet(s => s.Servers).Returns(new List<IManageableServer>());
        var operations = new BotReadOperations(servers.Object, AdminOperationTestServices.CreateExecutor());

        Assert.ThrowsAsync<ArgumentException>(async () => await operations.ListAsync(0, 101, CancellationToken.None).ConfigureAwait(false));
    }

    private static BotReadOperations.BotRow Row(string character, string state, double secondsInState)
        => new(1, "bot-" + character.ToLowerInvariant(), character, 10, 0, state, "test", secondsInState, $"{state} for a while (test)", DateTime.UnixEpoch);

    private static string?[] ReportedCharacters(JsonObject report)
        => report["bots"]!.AsArray().Select(b => b!["character"]?.GetValue<string>()).ToArray();
}
