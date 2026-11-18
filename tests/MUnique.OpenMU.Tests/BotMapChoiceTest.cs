// <copyright file="BotMapChoiceTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Bots;
using NUnit.Framework;
using MapChoice = MUnique.OpenMU.GameLogic.Bots.BotMapChoicePlanner.MapChoice;

/// <summary>
/// Tests for the rules which decide where a bot travels (see <see cref="BotMapChoicePlanner"/>): how good
/// a destination has to be, which warp entries are worth entering, when ground is a step down, and which
/// of the gathered candidates wins the pick.
/// </summary>
/// <remarks>
/// The candidates themselves are gathered by the navigator, which is the only place knowing what the bot
/// can safely fight and afford; these tests state the levels a bot would meet and check what the rules
/// make of them.
/// </remarks>
[TestFixture]
public class BotMapChoiceTest
{
    /// <summary>
    /// The bot's level in the tests.
    /// </summary>
    private const int BotLevel = 100;

    /// <summary>
    /// An escape takes any ground above the floor - a bot which cannot stay must not be picky - while a
    /// normal move has to beat the ground it stands on by a margin, so two maps don't leapfrog each other.
    /// </summary>
    [Test]
    public void EscapeTakesAnyGroundAboveTheFloor()
    {
        Assert.That(BotMapChoicePlanner.TargetLevel(escape: true, escapeFloor: 20, currentGroundLevel: 0), Is.EqualTo(20));
        Assert.That(BotMapChoicePlanner.TargetLevel(escape: true, escapeFloor: 20, currentGroundLevel: 90), Is.EqualTo(20), "the floor rules, not the ground left behind");
        Assert.That(BotMapChoicePlanner.TargetLevel(escape: true, escapeFloor: 0, currentGroundLevel: 90), Is.EqualTo(1), "some ground is always better than none");
        Assert.That(
            BotMapChoicePlanner.TargetLevel(escape: false, escapeFloor: 0, currentGroundLevel: 20),
            Is.EqualTo(20 + BotMapChoicePlanner.ImprovementMargin),
            "a normal move needs a real step up");
        Assert.That(
            BotMapChoicePlanner.TargetLevel(escape: false, escapeFloor: 0, currentGroundLevel: 0),
            Is.EqualTo(BotMapChoicePlanner.ImprovementMargin),
            "even from ground without a safe monster, the destination has to be worth the fare");
    }

    /// <summary>
    /// Monsters below both the target and the bot's own minimum can't make a map a candidate, so they
    /// need not be judged.
    /// </summary>
    [Test]
    public void MapsAreJudgedFromTheHigherMinimum()
    {
        Assert.That(BotMapChoicePlanner.JudgeFrom(minimumMonsterLevel: 0, targetLevel: 28), Is.EqualTo(28));
        Assert.That(BotMapChoicePlanner.JudgeFrom(minimumMonsterLevel: 40, targetLevel: 28), Is.EqualTo(40), "a mastered bot only counts what still pays it");
        Assert.That(BotMapChoicePlanner.JudgeFrom(minimumMonsterLevel: 40, targetLevel: 40), Is.EqualTo(40));
    }

    /// <summary>
    /// A warp entry is only entered when its part of the map reaches the target level, the bot can pay for
    /// it, and it lands on walkable tiles - a blocked gate would strand the bot.
    /// </summary>
    [Test]
    public void OnlyAffordableWalkableEntriesOfTheTargetLevelAreEntered()
    {
        Assert.That(BotMapChoicePlanner.IsWorthEntering(entryLevel: 30, targetLevel: 28, affordable: true, hasWalkableLanding: true), Is.True);
        Assert.That(BotMapChoicePlanner.IsWorthEntering(entryLevel: 28, targetLevel: 28, affordable: true, hasWalkableLanding: true), Is.True, "exactly the target level is enough");
        Assert.That(BotMapChoicePlanner.IsWorthEntering(entryLevel: 27, targetLevel: 28, affordable: true, hasWalkableLanding: true), Is.False);
        Assert.That(BotMapChoicePlanner.IsWorthEntering(entryLevel: 30, targetLevel: 28, affordable: false, hasWalkableLanding: true), Is.False, "a fare it cannot pay is no destination");
        Assert.That(BotMapChoicePlanner.IsWorthEntering(entryLevel: 30, targetLevel: 28, affordable: true, hasWalkableLanding: false), Is.False, "a blocked landing strands the bot");
    }

    /// <summary>
    /// Ground is a step down when it is weaker than where the bot stands, but stronger than every step
    /// down found before - one band down, not a retreat to the starter map.
    /// </summary>
    [Test]
    public void StepDownIsWeakerThanTheCurrentGroundButTheBestSoFar()
    {
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: 70, currentGroundLevel: 100, bestSoFar: 0), Is.True);
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: 70, currentGroundLevel: 100, bestSoFar: 70), Is.False, "already found");
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: 70, currentGroundLevel: 100, bestSoFar: 80), Is.False, "not the best step down");
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: 100, currentGroundLevel: 100, bestSoFar: 0), Is.False, "same ground is no step");
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: 120, currentGroundLevel: 100, bestSoFar: 0), Is.False, "stronger ground is no step down");
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: 0, currentGroundLevel: 100, bestSoFar: 0), Is.False, "nothing safe there");
    }

    /// <summary>
    /// With nothing safe where it stands, any huntable ground is a step down - the comparison must not
    /// rule out every map.
    /// </summary>
    [Test]
    public void WithoutSafeGroundAnyHuntableGroundIsAStepDown()
    {
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: BotLevel, currentGroundLevel: 0, bestSoFar: 0), Is.True);
        Assert.That(BotMapChoicePlanner.IsBetterStepDown(groundLevel: BotLevel, currentGroundLevel: 0, bestSoFar: BotLevel), Is.False);
    }

    /// <summary>
    /// No candidate means no move.
    /// </summary>
    [Test]
    public void NoCandidatesMeanNoMove()
    {
        Assert.That(BotMapChoicePlanner.PickStrongest([]), Is.Null);
    }

    /// <summary>
    /// A single candidate is taken - and the pick spreads the bots over the candidates it does have,
    /// instead of one map holding all of them.
    /// </summary>
    [Test]
    public void TheStrongestCandidateIsPickedAmongTheRunnersUp()
    {
        var only = CreateChoice(level: 10);
        Assert.That(BotMapChoicePlanner.PickStrongest([only]), Is.EqualTo(only));

        var strong = CreateChoice(level: 60);
        var weak = CreateChoice(level: 20);
        MapChoice[] candidates = [strong, weak];

        var picks = Enumerable.Range(0, 200).Select(_ => BotMapChoicePlanner.PickStrongest(candidates)).ToList();

        Assert.That(picks, Has.All.Matches<BotMapChoicePlanner.MapChoice?>(pick => candidates.Contains(pick!.Value)), "never a candidate outside the list");
        Assert.That(picks, Has.Some.EqualTo(strong), "the strongest ground is preferred, so it is picked");
        Assert.That(picks, Has.Some.EqualTo(weak), "but the runner-up is not worthless, so the population spreads");
    }

    /// <summary>
    /// Trivial ground is what the bot has to leave, and it has to beat it by a real step: the escape floor
    /// sits one level above the ground it leaves behind.
    /// </summary>
    [Test]
    public void TrivialGroundForcesAMoveOneBandUp()
    {
        var plan = BotMapChoicePlanner.PlanMove(Ready(groundLevel: 10, botLevel: 250));

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.Value.Escape, Is.True);
        Assert.That(plan.Value.EscapeFloor, Is.EqualTo(11), "anything safe beats staying, but not the very same band");
        Assert.That(plan.Value.Reason, Is.EqualTo(BotMapChoicePlanner.MapMoveReason.GroundTooWeak));
        Assert.That(plan.Value.AllowTrivialHome, Is.False, "the hometown is exactly the starter ground it has to leave");
    }

    /// <summary>
    /// A map without a single safe monster is hostile: the bot leaves regardless of its warp requirement,
    /// and any ground with a monster is better than that one.
    /// </summary>
    [Test]
    public void HostileGroundForcesAMoveBelowTheWarpRequirement()
    {
        var plan = BotMapChoicePlanner.PlanMove(Ready(groundLevel: 0, botLevel: 30) with { PlainLevel = 5, MinWarpLevel = 20 });

        Assert.That(plan, Is.Not.Null, "a bot which cannot fight back must not be held by its warp requirement");
        Assert.That(plan!.Value.Escape, Is.True);
        Assert.That(plan.Value.EscapeFloor, Is.EqualTo(1), "anything it can fight is better than nothing");
        Assert.That(plan.Value.AllowTrivialHome, Is.True, "with nothing safe to fight, going home is better than dying");
        Assert.That(plan.Value.Reason, Is.EqualTo(BotMapChoicePlanner.MapMoveReason.NothingSafeHere));
    }

    /// <summary>
    /// A bot which could not legally be on its map leaves it - e.g. a veteran stranded on a high map after
    /// its reset - and does not have to climb above the ground it stands on to do so.
    /// </summary>
    [Test]
    public void IllegalGroundForcesAMove()
    {
        var plan = BotMapChoicePlanner.PlanMove(Ready(groundLevel: 20, botLevel: 30) with { HasLegalWarpHere = false });

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.Value.Escape, Is.True);
        Assert.That(plan.Value.EscapeFloor, Is.EqualTo(1), "it moves to wherever it may be, not to better ground");
        Assert.That(plan.Value.Reason, Is.EqualTo(BotMapChoicePlanner.MapMoveReason.MayNotStayLegally));
    }

    /// <summary>
    /// The class hometown is where a bot may always be, even when no warp entry leads there - it is never a
    /// reason to leave.
    /// </summary>
    [Test]
    public void HomeMapForcesNoEscape()
    {
        var plan = BotMapChoicePlanner.PlanMove(Ready(groundLevel: 20, botLevel: 30) with { HasLegalWarpHere = false, IsHomeMap = true });

        Assert.That(plan?.Escape, Is.Not.True, "nothing forces it off its hometown");
    }

    /// <summary>
    /// The warp cooldown holds every move back, so a bot doesn't hop between maps on every tick.
    /// </summary>
    [Test]
    public void NoMoveWhileTheWarpCooldownRuns()
    {
        var plan = BotMapChoicePlanner.PlanMove(Ready(groundLevel: 10, botLevel: 250) with { WarpCooldownElapsed = false });

        Assert.That(plan, Is.Null);
    }

    /// <summary>
    /// Below its lowest warp requirement a bot only moves when it has to: ground which is neither trivial
    /// nor barren is no reason to leave.
    /// </summary>
    [Test]
    public void NoMoveBelowTheWarpRequirementOnNormalGround()
    {
        var facts = Ready(groundLevel: 20, botLevel: 30) with { PlainLevel = 5, MinWarpLevel = 20 };

        Assert.That(BotMapChoicePlanner.PlanMove(facts), Is.Null);
    }

    /// <summary>
    /// A map which pays nothing - no hit landed for minutes - lets the bot step DOWN to easier ground,
    /// even below its warp requirement.
    /// </summary>
    [Test]
    public void BarrenGroundAllowsAStepDown()
    {
        var facts = Ready(groundLevel: 20, botLevel: 30) with { PlainLevel = 5, MinWarpLevel = 20, Barren = true };

        var plan = BotMapChoicePlanner.PlanMove(facts);

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.Value.StepDown, Is.True);
        Assert.That(plan.Value.Escape, Is.False, "nothing forces it off the map, it just earns less than the fare");
        Assert.That(plan.Value.Reason, Is.EqualTo(BotMapChoicePlanner.MapMoveReason.GroundBarren));
    }

    /// <summary>
    /// A trivial map which offered nothing better the last time is not re-judged on every tick: the bot
    /// farms it and earns its way up, until it levels up or the review interval passes.
    /// </summary>
    [Test]
    public void DeferredReviewKeepsATrivialBotFromRejudging()
    {
        var facts = Ready(groundLevel: 10, botLevel: 250) with { EscapeReviewDeferred = true };

        Assert.That(BotMapChoicePlanner.PlanMove(facts), Is.Null);
        Assert.That(BotMapChoicePlanner.PlanMove(facts with { EscapeReviewDeferred = false }), Is.Not.Null, "a new level or a passed interval opens the review again");
    }

    /// <summary>
    /// The reason of a move is what the operator reads in the bot log.
    /// </summary>
    [Test]
    public void MoveReasonsAreDescribed()
    {
        Assert.That(BotMapChoicePlanner.Describe(BotMapChoicePlanner.MapMoveReason.NothingSafeHere), Is.EqualTo("nothing safe here"));
        Assert.That(BotMapChoicePlanner.Describe(BotMapChoicePlanner.MapMoveReason.MayNotStayLegally), Is.EqualTo("may not legally be here"));
        Assert.That(BotMapChoicePlanner.Describe(BotMapChoicePlanner.MapMoveReason.GroundTooWeak), Is.EqualTo("ground too weak"));
        Assert.That(BotMapChoicePlanner.Describe(BotMapChoicePlanner.MapMoveReason.GroundBarren), Is.EqualTo("barren"));
    }

    /// <summary>
    /// The facts of a bot which may move: in the world, on its own map, past the warp cooldown and above
    /// its warp requirement.
    /// </summary>
    /// <param name="groundLevel">The level of the strongest monster it can safely fight on its map.</param>
    /// <param name="botLevel">The bot's level.</param>
    /// <returns>The facts, to be adjusted by the test.</returns>
    private static BotMapChoicePlanner.MapMoveFacts Ready(int groundLevel, int botLevel)
        => new()
        {
            BotLevel = botLevel,
            PlainLevel = botLevel,
            MinWarpLevel = 1,
            CurrentGroundLevel = groundLevel,
            HasCurrentMap = true,
            HasLegalWarpHere = true,
            WarpCooldownElapsed = true,
        };

    /// <summary>
    /// Creates a map choice for the ranking tests; only its level matters to the rules.
    /// </summary>
    /// <param name="level">The level of the strongest safe monster of the choice.</param>
    /// <returns>The choice.</returns>
    private static BotMapChoicePlanner.MapChoice CreateChoice(int level)
        => new(
            new MUnique.OpenMU.Persistence.BasicModel.WarpInfo { Id = Guid.NewGuid(), Costs = 0, LevelRequirement = 0 },
            new MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition { Id = Guid.NewGuid(), Number = (byte)level },
            level);
}
