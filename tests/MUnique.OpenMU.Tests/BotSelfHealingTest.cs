// <copyright file="BotSelfHealingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests how a bot reacts to its AI failing: the engine's attribute system is not thread-safe, and a
/// lost race can corrupt a character's attribute graph for good - from then on every tick throws, the
/// bot stops playing and floods the log. It asks for a restart, which rebuilds the graph and heals it.
/// </summary>
[TestFixture]
public class BotSelfHealingTest
{
    /// <summary>
    /// A tick failing now and then is simply skipped, like before - no restart.
    /// </summary>
    [Test]
    public void SingleFailuresDoNotRestartTheBot()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        for (var i = 0; i < 100; i++)
        {
            bot.OnAiTickFailed();
            bot.OnAiTickSucceeded();
        }

        Assert.That(bot.AwaitsFaultRestart, Is.False);
    }

    /// <summary>
    /// A bot whose ticks keep failing is broken and asks the maintenance pass to restart it.
    /// </summary>
    [Test]
    public void PersistentFailuresRestartTheBot()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        for (var i = 0; i < 20; i++)
        {
            bot.OnAiTickFailed();
        }

        Assert.That(bot.AwaitsFaultRestart, Is.True);
    }

    /// <summary>
    /// A bot which is being stopped is not a broken bot - a shutdown must not look like a wave of failures and
    /// write off the bots it stops anyway.
    /// </summary>
    [Test]
    public void ATeardownIsNotAFault()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        for (var i = 0; i < 100; i++)
        {
            bot.OnAiTickFailed(new OperationCanceledException());
        }

        Assert.That(bot.AwaitsFaultRestart, Is.False);
        Assert.That(bot.Health.Faults, Is.Zero);
    }

    /// <summary>
    /// What made the last tick fail is kept, with the classification, so the log line and the operator surfaces
    /// say whether a restart is the documented cure (a corrupt graph) or an environment problem.
    /// </summary>
    [Test]
    public void TheFaultIsKeptForTheDiagnosis()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        bot.OnAiTickFailed(new NullReferenceException("Object reference not set to an instance of an object."));

        Assert.That(bot.Health.LastFault, Does.Contain("NullReferenceException"));
        Assert.That(bot.Health.LastFault, Does.Contain("corrupting"));
        Assert.That(bot.Health.Describe(), Does.Contain("1 faulted tick"));
    }

    /// <summary>
    /// A bot which keeps failing stops writing an error line per tick - the flood would bury the one line that
    /// matters, and the account's health record keeps the count either way.
    /// </summary>
    [Test]
    public void ARepeatingFailureIsNotLoggedPerTick()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        Assert.That(bot.ShouldLogAiTickFailure(), Is.True);
        for (var i = 0; i < 10; i++)
        {
            bot.OnAiTickFailed(new NullReferenceException());
        }

        Assert.That(bot.ShouldLogAiTickFailure(), Is.False);
        Assert.That(bot.Health.Faults, Is.EqualTo(10));
    }
}
