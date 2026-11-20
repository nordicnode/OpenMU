// <copyright file="BotDuelHandlerTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;

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
}
