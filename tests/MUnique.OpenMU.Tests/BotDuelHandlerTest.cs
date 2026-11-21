// <copyright file="BotDuelHandlerTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using Moq;

/// <summary>
/// Tests who a bot accepts a duel from: only when the operator enabled duels, the bot is free and the
/// player is a fair opponent.
/// </summary>
[TestFixture]
public class BotDuelHandlerTest
{
    private IGameContext _gameContext = null!;
    private OfflinePlayer _bot = null!;
    private Player _player = null!;
    private BotFeaturePlugIn _botPlugIn = null!;

    /// <summary>Creates a bot and a player on the same map.</summary>
    [SetUp]
    public async ValueTask SetUpAsync()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
        this._bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        await this._bot.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        this._bot.Account!.IsBot = true;
        this._bot.IsAlive = true;
        this._player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        await this._player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        this._player.IsAlive = true;
        this._botPlugIn = this._gameContext.FeaturePlugIns.GetPlugIn<BotFeaturePlugIn>() ?? new BotFeaturePlugIn();
        this._gameContext.FeaturePlugIns.AddPlugIn(this._botPlugIn, true);
        this._botPlugIn.Configuration = new BotConfiguration { Duels = true };
    }

    /// <summary>Duels are opt-in: without the configuration switch the bot never accepts.</summary>
    [Test]
    public void DoesNotAcceptWhileDuelsAreDisabled()
    {
        this._botPlugIn.Configuration!.Duels = false;

        Assert.That(BotDuelHandler.IsDuelEnabled(this._bot), Is.False);
        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False);
    }

    /// <summary>A free bot of a comparable level accepts the challenge of a human on its map.</summary>
    [Test]
    public void AcceptsAFairChallenge()
    {
        Assert.That(BotDuelHandler.IsDuelEnabled(this._bot), Is.True);
        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.True);
    }

    /// <summary>
    /// A bot does not duel while it is on an errand, and it turns down a player far above its level -
    /// a duel nobody can win is not worth the time of either side.
    /// </summary>
    [Test]
    public void TurnsDownBadChallenges()
    {
        this._player.Attributes![Stats.Level] = this._bot.Level + BotDuelHandler.MaximumLevelDifference + 1;
        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False, "too strong an opponent");

        this._player.Attributes[Stats.Level] = this._bot.Level;
        this._bot.IsOnShoppingTrip = true;
        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False, "busy with an errand");

        this._bot.IsOnShoppingTrip = false;
        this._bot.IsAlive = false;
        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False, "a dead bot has other concerns");
    }

    /// <summary>A challenge of a bot in another map is no challenge - the duel would need a warp first.</summary>
    [Test]
    public void TurnsDownAChallengeFromAnotherMap()
    {
        this._player.CurrentMap = null;

        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False);
    }

    /// <summary>
    /// A duel is not started from inside a town safezone: the target check already refuses a target
    /// which stands in one, and the bot itself has no business picking a fight between the merchants
    /// either.
    /// </summary>
    [Test]
    public void DoesNotDuelFromInsideASafezone()
    {
        var map = this._bot.CurrentMap!;
        Assert.That(this._bot.IsAtSafezone(), Is.False, "the fixture places the bot outside a safezone");
        Assert.That(this._player.IsAtSafezone(), Is.False);
        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.True);

        map.Terrain.SafezoneMap[this._bot.Position.X, this._bot.Position.Y] = true;

        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False, "a bot in a safezone keeps out of duels");
    }

    /// <summary>
    /// A real request reaches the bot with the duel room already attached
    /// (see <c>DuelActions.HandleDuelRequestAsync</c>) - the pending request itself must not count as
    /// "busy with a duel", or the bot would never answer one.
    /// </summary>
    [Test]
    public void AcceptsARealDuelRequest()
    {
        var room = new DuelRoom(new Mock<MUnique.OpenMU.DataModel.Configuration.DuelArea>().Object, this._player, this._bot)
        {
            State = DuelState.DuelRequested,
        };
        this._player.DuelRoom = room;
        this._bot.DuelRoom = room;

        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.True);
    }

    /// <summary>A bot which already fights a duel does not accept another one.</summary>
    [Test]
    public void DoesNotAcceptADuelWhileOneRuns()
    {
        this.CreateDuel(DuelState.DuelStarted);

        Assert.That(BotDuelHandler.CanAccept(this._bot, this._player), Is.False);
    }

    /// <summary>
    /// A challenge the bot sent itself is the other side's turn to answer - it neither counts as free
    /// nor as somebody the bot may accept a request from.
    /// </summary>
    [Test]
    public async ValueTask DoesNotAcceptARequestItSentItselfAsync()
    {
        var other = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        other.IsAlive = true;
        var room = new DuelRoom(new Mock<MUnique.OpenMU.DataModel.Configuration.DuelArea>().Object, this._bot, other)
        {
            State = DuelState.DuelRequested,
        };
        this._bot.DuelRoom = room;
        other.DuelRoom = room;

        Assert.That(BotDuelHandler.CanAccept(this._bot, other), Is.False, "the bot is the requester here");
        Assert.That(await BotDuelHandler.TryAcceptAsync(this._bot, other).ConfigureAwait(false), Is.False);
    }

    /// <summary>
    /// A third party which hits a duelling bot is an ordinary aggressor: the duel only excuses the duel
    /// partner's hits, not anybody else's.
    /// </summary>
    [Test]
    public async ValueTask ThirdPartyStaysAnAggressorDuringADuelAsync()
    {
        this.CreateDuel(DuelState.DuelStarted);
        var thirdParty = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        thirdParty.IsAlive = true;
        var plugIn = new BotSelfDefensePlugIn();

        plugIn.AttackableGotHit(this._bot, this._player, new HitInfo(1, 0, DamageAttributes.Undefined));
        Assert.That(this._bot.RecentAggressor, Is.Null, "the duel partner is no aggressor");

        plugIn.AttackableGotHit(this._bot, thirdParty, new HitInfo(1, 0, DamageAttributes.Undefined));
        Assert.That(this._bot.RecentAggressor, Is.SameAs(thirdParty), "a third party is");
    }

    /// <summary>
    /// A death at the hands of a third party in a duel is remembered, even though the duel partner's
    /// kill is not.
    /// </summary>
    [Test]
    public async ValueTask ThirdPartyKillDuringADuelArmsRevengeAsync()
    {
        this.CreateDuel(DuelState.DuelStarted);
        var thirdParty = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        thirdParty.IsAlive = true;
        var plugIn = new BotRevengePlugIn();

        await plugIn.AttackableGotKilledAsync(this._bot, this._player).ConfigureAwait(false);
        Assert.That(this._bot.GetRecentDeathCount(this._player), Is.Zero, "the duel partner's kill is no wrong");

        await plugIn.AttackableGotKilledAsync(this._bot, thirdParty).ConfigureAwait(false);
        Assert.That(this._bot.GetRecentDeathCount(thirdParty), Is.EqualTo(1));
    }

    /// <summary>
    /// A running duel makes the partner a legal target: the game exempts the kill from the player
    /// killer penalty, so the bot can actually fight the duel it accepted.
    /// </summary>
    [Test]
    public void RunningDuelMakesThePartnerALegalTarget()
    {
        this.CreateDuel(DuelState.DuelStarted);

        Assert.That(BotDuelHandler.GetOpponent(this._bot), Is.SameAs(this._player));
        Assert.That(BotDuelHandler.GetOpponent(this._player), Is.SameAs(this._bot));
        Assert.That(BotPvpRules.IsLegalPvpTarget(this._bot, this._player), Is.True);
    }

    /// <summary>
    /// The countdown before the duel starts is no fight yet - the game only exempts kills once the
    /// duel is running, so a strike now would make the bot an outlaw.
    /// </summary>
    [Test]
    public void CountdownDoesNotMakeThePartnerALegalTarget()
    {
        this.CreateDuel(DuelState.DuelAccepted);

        Assert.That(BotPvpRules.IsLegalPvpTarget(this._bot, this._player), Is.False);
    }

    /// <summary>A duel is between two players: it does not legalize anybody else for the bot.</summary>
    [Test]
    public async ValueTask DuelDoesNotLegalizeABystanderAsync()
    {
        this.CreateDuel(DuelState.DuelStarted);
        var bystander = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        bystander.IsAlive = true;

        Assert.That(BotPvpRules.IsLegalPvpTarget(this._bot, bystander), Is.False);
    }

    /// <summary>
    /// A death in a duel is part of the game, not a wrong: the bot neither holds a grudge against its
    /// partner nor puts it on its death list.
    /// </summary>
    [Test]
    public async ValueTask DuelLeavesNoGrudgeAsync()
    {
        this.CreateDuel(DuelState.DuelStarted);

        new BotSelfDefensePlugIn().AttackableGotHit(this._bot, this._player, new HitInfo(1, 0, DamageAttributes.Undefined));
        Assert.That(this._bot.RecentAggressor, Is.Null, "a duel is no attack which needs self-defense");

        await new BotRevengePlugIn().AttackableGotKilledAsync(this._bot, this._player).ConfigureAwait(false);
        Assert.That(this._bot.HasRevengeIntent, Is.False, "a duel is no wrong to avenge");
        Assert.That(this._bot.GetRecentDeathCount(this._player), Is.Zero);
    }

    /// <summary>
    /// Puts the bot and the player into the same duel room in the given state.
    /// </summary>
    /// <param name="state">The state of the duel.</param>
    /// <returns>The duel room.</returns>
    private DuelRoom CreateDuel(DuelState state)
    {
        var room = new DuelRoom(new Mock<MUnique.OpenMU.DataModel.Configuration.DuelArea>().Object, this._bot, this._player) { State = state };
        this._bot.DuelRoom = room;
        this._player.DuelRoom = room;
        return room;
    }
}
