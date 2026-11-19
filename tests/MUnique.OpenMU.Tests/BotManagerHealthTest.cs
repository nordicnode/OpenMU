// <copyright file="BotManagerHealthTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;

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
