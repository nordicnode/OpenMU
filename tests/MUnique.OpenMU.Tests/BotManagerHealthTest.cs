// <copyright file="BotManagerHealthTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Tests that the health record belongs to the character and not to the bot instance. A restart throws the bot
/// away and spawns a new one for the same character, and only a record which survives that can tell a bot which
/// healed from one which breaks itself again within minutes - the difference between one restart and a loop.
/// The record is per character, not per account, because several characters of one account may run at once.
/// </summary>
[TestFixture]
public class BotManagerHealthTest
{
    /// <summary>
    /// The same character keeps its record across restarts; a different character or account does not inherit it.
    /// </summary>
    [Test]
    public void TheHealthRecordBelongsToTheCharacter()
    {
        var manager = new BotManager();

        var first = manager.HealthFor("bot-account", 0);
        var second = manager.HealthFor("bot-account", 0);
        var sameAccountOtherCharacter = manager.HealthFor("bot-account", 1);
        var other = manager.HealthFor("another-account", 0);

        Assert.That(second, Is.SameAs(first));
        Assert.That(sameAccountOtherCharacter, Is.Not.SameAs(first));
        Assert.That(other, Is.Not.SameAs(first));
    }

    /// <summary>Stopped characters retain resettable records without affecting other accounts.</summary>
    [Test]
    public void ResetsRetainedHealthByAccount()
    {
        var manager = new BotManager();
        var first = manager.HealthFor("bot-account", 0);
        var second = manager.HealthFor("bot-account", 1);
        var other = manager.HealthFor("other-account", 0);
        first.RecordFault(new InvalidOperationException());
        second.RecordFault(new InvalidOperationException());
        other.RecordFault(new InvalidOperationException());
        for (var i = 0; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            first.TryBeginRestart();
        }

        var result = manager.ResetHealth("BOT-ACCOUNT");

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Single(r => r.Slot == 0).QuarantinedSeconds, Is.GreaterThan(0));
        Assert.That(first.Faults, Is.Zero);
        Assert.That(first.RemainingQuarantine(), Is.EqualTo(TimeSpan.Zero));
        Assert.That(second.Faults, Is.Zero);
        Assert.That(other.Faults, Is.EqualTo(1));
        Assert.That(manager.HealthFor("bot-account", 0), Is.SameAs(first));
    }

    /// <summary>The feature resets the manager of the requested server even when it has no live bots.</summary>
    [Test]
    public void FeatureResetsStoppedBotHealth()
    {
        var feature = new BotFeaturePlugIn();
        var context = new Mock<IGameContext>();
        var otherContext = new Mock<IGameContext>();
        var health = feature.ManagerFor(context.Object).HealthFor("bot-account", 2);
        var other = feature.ManagerFor(otherContext.Object).HealthFor("bot-account", 2);
        health.RecordFault(new InvalidOperationException());
        other.RecordFault(new InvalidOperationException());

        var reset = feature.ResetHealth(context.Object, "BOT-ACCOUNT");

        Assert.That(reset.Single().Slot, Is.EqualTo(2));
        Assert.That(reset.Single().Faults, Is.EqualTo(1));
        Assert.That(health.Faults, Is.Zero);
        Assert.That(other.Faults, Is.EqualTo(1));
    }

    /// <summary>Concurrent duplicate spawns do not create a second persistence context.</summary>
    [Test]
    public async Task ReservesSlotBeforeLoadingAccountAsync()
    {
        var load = new TaskCompletionSource<Account?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playerContext = new Mock<IPlayerContext>();
        playerContext.Setup(p => p.GetAccountByLoginNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<Account?>(load.Task));
        var persistence = new Mock<IPersistenceContextProvider>();
        persistence.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(playerContext.Object);
        var context = new Mock<IGameContext>();
        context.SetupGet(c => c.LoggerFactory).Returns(NullLoggerFactory.Instance);
        context.SetupGet(c => c.Configuration).Returns(new GameConfiguration());
        context.SetupGet(c => c.PersistenceContextProvider).Returns(persistence.Object);
        var manager = new BotManager();

        var first = manager.SpawnBotAsync(context.Object, "account", 0).AsTask();
        Assert.That(await manager.SpawnBotAsync(context.Object, "ACCOUNT", 0).ConfigureAwait(false), Is.False);
        Assert.That(await manager.SpawnBotAsync(context.Object, "account").ConfigureAwait(false), Is.False);
        persistence.Verify(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>()), Times.Once);
        load.SetResult(null);
        Assert.That(await first.ConfigureAwait(false), Is.False);

        // The reservation is released after failure.
        Assert.That(await manager.SpawnBotAsync(context.Object, "account", 0).ConfigureAwait(false), Is.False);
        persistence.Verify(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>()), Times.Exactly(2));
    }

    /// <summary>
    /// The escalation of a restart is what the next bot instance of the character sees - the counter is not reset by
    /// the restart which the manager performs.
    /// </summary>
    [Test]
    public void ARestartKeepsTheRestartHistory()
    {
        var manager = new BotManager();
        var health = manager.HealthFor("bot-account", 0);
        for (var i = 0; i < BotHealthPolicy.RestartsUntilQuarantine; i++)
        {
            health.TryBeginRestart();
        }

        // What a freshly spawned BotPlayer for the same character would get:
        Assert.That(manager.HealthFor("bot-account", 0).Restarts, Is.EqualTo(BotHealthPolicy.RestartsUntilQuarantine));
        Assert.That(manager.HealthFor("bot-account", 0).RemainingQuarantine(), Is.GreaterThan(TimeSpan.Zero));
        Assert.That(manager.HealthFor("bot-account", 1).Restarts, Is.Zero);
    }
}
