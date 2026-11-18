// <copyright file="BotFlappingAlarmTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Operations;

using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>
/// Tests the alarm about the bots which keep breaking themselves. A bot which was restarted again and again is in
/// the world and looks busy while it plays badly - the alarm is what turns that into something an operator sees.
/// </summary>
[TestFixture]
public class BotFlappingAlarmTest
{
    /// <summary>
    /// A bot which was restarted a couple of times is still within what a restart can heal; from the threshold on,
    /// the restarts themselves are the story - which is where the game logic starts quarantining instead.
    /// </summary>
    [Test]
    public void OnlyRepeatedRestartsOrAQuarantineCount()
    {
        var healthy = new BotFlappingAlarm.FlappingBot("healthy", "Kain", BotFlappingAlarm.RestartsThreshold - 1, 0);
        var twiceRestarted = new BotFlappingAlarm.FlappingBot("twice", "Lena", BotFlappingAlarm.RestartsThreshold, 0);
        var quarantined = new BotFlappingAlarm.FlappingBot("quarantined", "Mo", 0, 120);

        var found = BotFlappingAlarm.Find([healthy, twiceRestarted, quarantined]);

        Assert.That(found.Select(bot => bot.LoginName), Is.EquivalentTo(new[] { "twice", "quarantined" }));
    }

    /// <summary>
    /// The bot which is held back from its next restart right now comes first, then the one with more restarts
    /// behind it - the order an operator would triage in.
    /// </summary>
    [Test]
    public void OrdersTheLongestQuarantineFirst()
    {
        var soon = new BotFlappingAlarm.FlappingBot("soon", "Kain", 5, 60);
        var longAgo = new BotFlappingAlarm.FlappingBot("long-ago", "Lena", 3, 0);
        var longest = new BotFlappingAlarm.FlappingBot("longest", "Mo", 3, 600);
        var moreRestarts = new BotFlappingAlarm.FlappingBot("more-restarts", "Nia", 9, 60);

        var found = BotFlappingAlarm.Find([soon, longAgo, longest, moreRestarts]);

        Assert.That(
            found.Select(bot => bot.LoginName),
            Is.EqualTo(new[] { "longest", "more-restarts", "soon", "long-ago" }));
    }

    /// <summary>
    /// The sentence names the accounts an operator has to look at, with what is known about them.
    /// </summary>
    [Test]
    public void DescribesTheBotsWhichKeepFailing()
    {
        var flapping = new BotFlappingAlarm.FlappingBot("bot-a", "Kain", 4, 180);
        var alsoFlapping = new BotFlappingAlarm.FlappingBot("bot-b", "Lena", 3, 0);

        var sentence = BotFlappingAlarm.Describe([flapping, alsoFlapping], 20);

        Assert.That(sentence, Is.EqualTo("2 of 20 bots keep failing: bot-a (4 restarts, quarantined for another 3 minutes), bot-b (3 restarts)."));
    }

    /// <summary>
    /// One bot is described in the singular - the sentence is read by a human.
    /// </summary>
    [Test]
    public void DescribesOneBotInTheSingular()
    {
        var flapping = new BotFlappingAlarm.FlappingBot("bot-a", "Kain", 0, 30);

        Assert.That(
            BotFlappingAlarm.Describe([flapping], 7),
            Is.EqualTo("1 of 7 bots keeps failing: bot-a (quarantined for another 1 minutes)."));
    }

    /// <summary>
    /// A long list of flapping bots is counted instead of named, so the banner stays one line.
    /// </summary>
    [Test]
    public void CountsTheBotsItDoesNotName()
    {
        var bots = Enumerable.Range(0, BotFlappingAlarm.NamesInSentence + 2)
            .Select(i => new BotFlappingAlarm.FlappingBot($"bot-{i:00}", null, 3, 0))
            .ToList();

        var sentence = BotFlappingAlarm.Describe(bots, 50);

        Assert.That(sentence, Does.Contain("and 2 more"));
        Assert.That(sentence, Does.Not.Contain($"bot-{BotFlappingAlarm.NamesInSentence:00}"));
    }

    /// <summary>
    /// A falling back to the character name: an account which could not be read still has to be named, and a bot
    /// which is fine says nothing at all.
    /// </summary>
    [Test]
    public void NamesTheCharacterWhenTheAccountIsUnknownAndIsSilentWhenNoBotFlags()
    {
        Assert.That(
            BotFlappingAlarm.Describe([new BotFlappingAlarm.FlappingBot(null, "Kain", 3, 0)], 1),
            Is.EqualTo("1 of 1 bots keeps failing: Kain (3 restarts)."));

        Assert.That(BotFlappingAlarm.Describe([], 30), Is.Empty);
    }
}
