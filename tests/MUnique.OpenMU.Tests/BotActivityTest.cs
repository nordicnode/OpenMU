// <copyright file="BotActivityTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Administration;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests what a bot reports about itself to the administration surfaces - the panel column, the live player
/// projection and the <c>bots_list</c> tool all read <see cref="BotPlayer.Activity"/>.
/// </summary>
[TestFixture]
public class BotActivityTest
{
    /// <summary>
    /// A bot which isn't navigating reports nothing, so the surfaces show an empty cell instead of a state
    /// which belongs to no navigation. Its navigation only exists while it is in the world.
    /// </summary>
    [Test]
    public void ABotWhichIsNotNavigatingReportsNothing()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        Assert.That(bot.Activity, Is.Null);
        Assert.That(bot.Activity?.Summary, Is.Null);
        Assert.That(bot.Activity?.Detail, Is.Null);
        Assert.That(bot.Activity?.State, Is.Null);
    }

    /// <summary>
    /// A bot which entered the world reports the mode it is in, why, for how long and the long form - the
    /// whole state surface, not just the null case above - and keeps reporting it while it navigates.
    /// </summary>
    [Test]
    public async Task ABotInTheWorldReportsWhatItIsDoingAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        await using var bot = await BotPlayerFixture.CreateBotAsync(gameContext).ConfigureAwait(false);

        var activity = bot.Activity;
        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.State, Is.EqualTo(nameof(BotState.Idle)));
        Assert.That(activity.Reason, Is.EqualTo("started"));
        Assert.That(activity.Summary, Does.StartWith("Idle for ").And.Contain("(started)"));
        Assert.That(activity.Detail, Does.Contain("Idle"));
        Assert.That(activity.Meeting, Is.Null);
        Assert.That(activity.LastMeeting, Is.Null);

        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var later = bot.Activity;
        Assert.That(later, Is.Not.Null);
        Assert.That(later!.TimeInState, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4)));
        Assert.That(BotNavigator.DeclaredClaims.Select(state => state.ToString()), Does.Contain(later.State), "the reported mode must be one the tick can claim");
    }

    /// <summary>
    /// A bot which announced an event meeting and set off reports that meeting with its mode, so the
    /// administration surfaces can show where it is going and how long it still has, not just that it is in
    /// the event-meeting mode.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ReportsTheEventMeetingTheBotIsOnAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        await using var bot = await BotPlayerFixture.CreateBotAsync(gameContext).ConfigureAwait(false);
        bot.EventMeeting = new BotEventMeeting(
            new GameMapDefinition { Number = 4, Name = "Lost Tower" },
            new Point(200, 200),
            DateTime.UtcNow.AddMinutes(5));
        bot.EventMeeting.AdvanceTo(BotMeetingState.Traveling);
        bot.LastEventMeeting = new BotMeetingOutcome(nameof(BotMeetingState.Expired), "Devias 215, 40", null, null);

        var activity = bot.Activity;

        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.Meeting, Is.Not.Null);
        Assert.That(activity.Meeting!.State, Is.EqualTo(nameof(BotMeetingState.Traveling)));
        Assert.That(activity.Meeting.Place, Is.EqualTo("Lost Tower 200, 200"));
        Assert.That(activity.Meeting.Summary, Does.StartWith("Traveling to Lost Tower 200, 200"));
        Assert.That(activity.LastMeeting, Is.Not.Null);
        Assert.That(activity.LastMeeting!.State, Is.EqualTo(nameof(BotMeetingState.Expired)));
        Assert.That(activity.Detail, Does.Contain("meeting traveling"));
        Assert.That(activity.Detail, Does.Not.Contain("last meeting"), "the active meeting is the one the long form explains");
    }

    /// <summary>
    /// A bot whose meeting is over keeps reporting how it ended, also in the long form which the state command
    /// and the table tooltip show - so the outcome is not only in the log.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async Task ReportsTheOutcomeOfAnEndedMeetingAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        await using var bot = await BotPlayerFixture.CreateBotAsync(gameContext).ConfigureAwait(false);
        bot.LastEventMeeting = new BotMeetingOutcome(
            nameof(BotMeetingState.Abandoned),
            "Lost Tower 200, 200",
            "Blood Castle",
            "no route to the entry NPC");

        var activity = bot.Activity;

        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.LastMeeting, Is.Not.Null);
        Assert.That(activity.LastMeeting!.Summary, Is.EqualTo("Last meeting: abandoned at Lost Tower 200, 200"));
        Assert.That(activity.Detail, Does.Contain("last meeting abandoned at Lost Tower 200, 200 (Blood Castle) - no route to the entry NPC"));
    }

    /// <summary>
    /// The started bot really is a player of its game context - the administration surfaces find the bots of a
    /// server by walking its players, so a bot which is not in that list is invisible to them.
    /// </summary>
    [Test]
    public async Task AStartedBotIsAPlayerOfItsGameContextAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        await using var bot = await BotPlayerFixture.CreateBotAsync(gameContext).ConfigureAwait(false);

        var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);

        Assert.That(players, Does.Contain(bot));
        Assert.That(bot.SelectedCharacter?.Name, Is.EqualTo(BotPlayerFixture.DefaultCharacterName));
    }

    /// <summary>
    /// The live player projection - what the admin panel and the MCP tools read - carries the bot's state for a
    /// real bot, not just for the fake players the surface tests use.
    /// </summary>
    [Test]
    public async Task TheLivePlayerProjectionCarriesTheStateOfARealBotAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        await using var bot = await BotPlayerFixture.CreateBotAsync(gameContext).ConfigureAwait(false);
        var administration = new GameServerAdministration(gameContext);

        var info = await administration.GetPlayerAsync(BotPlayerFixture.DefaultCharacterName, CancellationToken.None).ConfigureAwait(false);

        Assert.That(info, Is.Not.Null, "a bot in the world is an available live player");
        Assert.That(info!.Account, Is.EqualTo(BotPlayerFixture.DefaultLoginName));
        Assert.That(info.BotState, Is.EqualTo(bot.Activity!.Summary));
        Assert.That(info.BotState, Does.StartWith("Idle for "));
    }
}
