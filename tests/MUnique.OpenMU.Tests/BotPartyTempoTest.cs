// <copyright file="BotPartyTempoTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests <see cref="BotPartyTempo"/>: while a human party leader runs ahead to pull, the bots hold their
/// fire and follow first - but only when there is a human leader, the bot stands far enough behind and
/// the bot is not already in a fight of its own.
/// </summary>
[TestFixture]
public class BotPartyTempoTest
{
    private IGameContext _gameContext = null!;

    /// <summary>Creates a fresh game context for every test.</summary>
    [SetUp]
    public void SetUp() => this._gameContext = GameContextTestHelper.CreateGameContext();

    /// <summary>
    /// A bot far behind its walking leader holds fire; a bot within the pull distance, a bot which is
    /// already fighting and a leader which stands still are no reason to hold.
    /// </summary>
    [Test]
    public async ValueTask HoldsFireWhileTheLeaderRunsAheadAsync()
    {
        var (bot, leader) = await this.CreatePartyAsync().ConfigureAwait(false);
        bot.Position = new Point(100, 100);
        leader.Position = new Point(100 + BotPartyTempo.PullDistance + 8, 100);
        await StartWalkingAsync(leader).ConfigureAwait(false);

        Assert.That(BotPartyTempo.ShouldHoldFire(bot, hasEngagedTarget: false), Is.True);

        leader.Position = new Point(100 + BotPartyTempo.PullDistance - 1, 100);
        Assert.That(BotPartyTempo.ShouldHoldFire(bot, hasEngagedTarget: false), Is.False, "the group is together");

        leader.Position = new Point(100 + BotPartyTempo.PullDistance + 8, 100);
        Assert.That(BotPartyTempo.ShouldHoldFire(bot, hasEngagedTarget: true), Is.False, "the bot is already fighting");

        await leader.StopWalkingAsync().ConfigureAwait(false);
        Assert.That(BotPartyTempo.ShouldHoldFire(bot, hasEngagedTarget: false), Is.False, "a standing leader is no pull");
    }

    /// <summary>
    /// A party of bots keeps its own tempo, and a bot without a party is not held back by a leader it
    /// does not have.
    /// </summary>
    [Test]
    public async ValueTask OnlyAHumanLeaderSetsTheTempoAsync()
    {
        var (bot, _) = await this.CreatePartyAsync(useBotLeader: true).ConfigureAwait(false);
        Assert.That(BotPartyTempo.ShouldHoldFire(bot, hasEngagedTarget: false), Is.False, "bot-only parties keep their tempo");

        bot.Party = null;
        Assert.That(BotPartyTempo.ShouldHoldFire(bot, hasEngagedTarget: false), Is.False);
    }

    private async ValueTask<(OfflinePlayer Bot, Player Leader)> CreatePartyAsync(bool useBotLeader = false)
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        bot.Account!.IsBot = true;
        bot.IsAlive = true;

        Player leader = useBotLeader
            ? await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false)
            : await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        leader.IsAlive = true;
        if (useBotLeader)
        {
            leader.Account!.IsBot = true;
        }

        var party = new Party(new PartyManager(5, new NullLogger<Party>()), 5, new NullLogger<Party>());
        Assert.That(await party.AddAsync(leader).ConfigureAwait(false), Is.True);
        Assert.That(await party.AddAsync(bot).ConfigureAwait(false), Is.True);
        leader.Party = party;
        bot.Party = party;

        return (bot, leader);
    }

    private static async ValueTask StartWalkingAsync(Player leader)
    {
        var from = leader.Position;
        var to = new Point((byte)(from.X + 1), from.Y);
        WalkingStep[] steps = [new() { From = from, To = to, Direction = Direction.East }];
        await leader.WalkToAsync(to, steps).ConfigureAwait(false);
    }
}
