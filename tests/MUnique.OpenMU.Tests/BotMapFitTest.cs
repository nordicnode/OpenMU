// <copyright file="BotMapFitTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;
using NUnit.Framework;
using MonsterAttribute = MUnique.OpenMU.Persistence.BasicModel.MonsterAttribute;
using MonsterDefinition = MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition;
using MonsterSpawnArea = MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea;

/// <summary>
/// Tests for the ground a bot picks when the one it stands on has become too weak for it - the case of the
/// level 200-300 bots which kept farming level-1 monsters in Lorencia (issue #1542).
/// </summary>
/// <remarks>
/// The navigator judges a map by the strongest monster the bot can safely fight there
/// (<see cref="CombatHandler.IsSafeTarget"/>), so the tests state the monster levels of a small
/// configuration and check which of those maps the bot is willing to travel to.
/// </remarks>
[TestFixture]
public class BotMapFitTest
{
    /// <summary>
    /// The monster numbers are unique and never reused, because <see cref="CombatHandler"/> caches a
    /// monster's combat stats statically by its number.
    /// </summary>
    private const short CurrentGroundMonsterNumber = 30110;
    private const short WeakerGroundMonsterNumber = 30111;
    private const short BetterGroundMonsterNumber = 30112;
    private const short BestGroundMonsterNumber = 30113;

    private const int CurrentGroundLevel = 10;
    private const int WeakerGroundLevel = 5;
    private const int BetterGroundLevel = 20;
    private const int BestGroundLevel = 60;

    /// <summary>
    /// The bot's level in the scenarios - high above the monsters of its current ground, as in the report.
    /// </summary>
    private const int BotLevel = 100;

    /// <summary>
    /// Ground whose strongest safe monster is below half the bot's level is trivial: the hunt there can not
    /// even pay for the fare of a warp.
    /// </summary>
    [Test]
    public void GroundWellBelowTheBotsLevelIsTrivial()
    {
        Assert.That(MapFit.IsTrivial(bestSafeMonsterLevel: 20, botLevel: 250), Is.True);
        Assert.That(MapFit.IsTrivial(bestSafeMonsterLevel: 125, botLevel: 250), Is.False, "exactly half is still worth hunting");
        Assert.That(MapFit.IsTrivial(bestSafeMonsterLevel: 130, botLevel: 250), Is.False);
        Assert.That(MapFit.IsTrivial(bestSafeMonsterLevel: 0, botLevel: 250), Is.False, "a map without a safe monster is hostile, not trivial");
    }

    /// <summary>
    /// An escape target must never offer less than the ground the bot leaves, and leaving trivial ground
    /// demands a real step up. Otherwise a bot which leaves the starter map lands on the next starter map
    /// and is back where it started.
    /// </summary>
    [Test]
    public void EscapeTargetMustBeatTheGroundTheBotLeaves()
    {
        Assert.That(MapFit.EscapeTargetFloor(currentLevel: 19, mayNotStayAnyway: false), Is.GreaterThan(19));
        Assert.That(MapFit.EscapeTargetFloor(currentLevel: 0, mayNotStayAnyway: false), Is.EqualTo(1), "nothing safe here: anything safe beats it");
        Assert.That(MapFit.EscapeTargetFloor(currentLevel: 140, mayNotStayAnyway: true), Is.EqualTo(1), "a map it may not be on is left for any safe ground");
    }

    /// <summary>
    /// The reported situation: a bot of level <see cref="BotLevel"/> stands on ground with level
    /// <see cref="CurrentGroundLevel"/> monsters. The maps offered to it as an escape must be better than
    /// that ground - the map with the even weaker monsters (Noria next to Lorencia in the report) must not
    /// be offered, or the bot warps in circles between the starter maps of the same level band.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask EscapeSkipsGroundBelowTheCurrentOneAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await CreateHunterAsync(gameContext).ConfigureAwait(false);
        var current = AddHuntingMap(gameContext, 1, CurrentGroundMonsterNumber, CurrentGroundLevel);
        AddHuntingMap(gameContext, 2, WeakerGroundMonsterNumber, WeakerGroundLevel);
        AddHuntingMap(gameContext, 3, BetterGroundMonsterNumber, BetterGroundLevel);
        AddHuntingMap(gameContext, 4, BestGroundMonsterNumber, BestGroundLevel);
        bot.SetCurrentMapSilently(new GameMap(current, TimeSpan.FromMinutes(1), 8));

        var floor = MapFit.EscapeTargetFloor(CurrentGroundLevel, mayNotStayAnyway: false);
        var candidates = new BotNavigator(bot).GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: floor);

        Assert.That(candidates.Select(c => c.Level), Is.EquivalentTo(new[] { BetterGroundLevel, BestGroundLevel }));
        Assert.That(
            candidates.Select(c => c.Map.Number),
            Has.None.EqualTo(current.Number),
            "the map the bot stands on is no escape target");
    }

    /// <summary>
    /// A bot which has to leave anyway - its ground holds nothing it can safely fight, or it may not legally
    /// be on that map - accepts any map with something safe to hunt. That keeps a freshly reset veteran out
    /// of a map it can no longer survive instead of letting it die there.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask EscapeFromAHostileGroundAcceptsAnySafeGroundAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await CreateHunterAsync(gameContext).ConfigureAwait(false);
        var current = AddHuntingMap(gameContext, 1, CurrentGroundMonsterNumber, CurrentGroundLevel);
        AddHuntingMap(gameContext, 2, WeakerGroundMonsterNumber, WeakerGroundLevel);
        bot.SetCurrentMapSilently(new GameMap(current, TimeSpan.FromMinutes(1), 8));

        var candidates = new BotNavigator(bot).GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: 1);

        Assert.That(candidates.Select(c => c.Level), Is.EquivalentTo(new[] { WeakerGroundLevel }));
    }

    /// <summary>
    /// When nothing better is reachable, the bot gets no target at all and stays where it is: it farms the
    /// ground it stands on until its levels and gear open the next map, instead of shuffling between maps of
    /// no better ground. Traveling to the same-or-weaker map is what burned its fares and kept it in the
    /// starter band.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask NothingBetterThanTrivialGroundIsNoTargetAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await CreateHunterAsync(gameContext).ConfigureAwait(false);
        var current = AddHuntingMap(gameContext, 1, CurrentGroundMonsterNumber, CurrentGroundLevel);
        AddHuntingMap(gameContext, 2, WeakerGroundMonsterNumber, WeakerGroundLevel);
        bot.SetCurrentMapSilently(new GameMap(current, TimeSpan.FromMinutes(1), 8));

        var floor = MapFit.EscapeTargetFloor(CurrentGroundLevel, mayNotStayAnyway: false);
        var candidates = new BotNavigator(bot).GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: floor);

        Assert.That(candidates, Is.Empty);
    }

    /// <summary>
    /// Going home is no escape: for a high-level character its class hometown is exactly the starter ground
    /// it has to leave. Only an errand which needs a town (shopping, the buff NPC, leaving the market) may
    /// send it there.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask HomeEscapeRefusesATrivialHometownAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await CreateHunterAsync(gameContext).ConfigureAwait(false);
        var home = AddHuntingMap(gameContext, 1, CurrentGroundMonsterNumber, CurrentGroundLevel);
        var elsewhere = AddHuntingMap(gameContext, 2, BetterGroundMonsterNumber, BetterGroundLevel);
        Mock.Get(bot.SelectedCharacter!.CharacterClass!).Setup(c => c.HomeMap).Returns(home);
        bot.SetCurrentMapSilently(new GameMap(elsewhere, TimeSpan.FromMinutes(1), 8));

        var navigator = new BotNavigator(bot);

        Assert.That(
            navigator.TryGetHomeEscapeGate(allowTrivialHome: false, out _, out _, out _),
            Is.False,
            "a bot of this level must not be sent home to farm its level-1 monsters");
        Assert.That(
            navigator.TryGetHomeEscapeGate(allowTrivialHome: true, out var gate, out var target, out _),
            Is.True,
            "an errand which needs a town may still go home");
        Assert.That(gate, Is.Not.Null);
        Assert.That(target, Is.SameAs(home));
    }

    /// <summary>
    /// A review without result is remembered, so the bot farms instead of re-judging every map on every
    /// tick; a level-up lifts it at once, because the verdict was made for the old level.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask EscapeReviewIsDeferredUntilTheLevelChangesAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await CreateHunterAsync(gameContext).ConfigureAwait(false);
        var current = AddHuntingMap(gameContext, 1, CurrentGroundMonsterNumber, CurrentGroundLevel);
        bot.SetCurrentMapSilently(new GameMap(current, TimeSpan.FromMinutes(1), 8));
        var timeProvider = new TestTimeProvider { UtcNow = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        var other = AddHuntingMap(gameContext, 2, WeakerGroundMonsterNumber, WeakerGroundLevel);
        var navigator = new BotNavigator(bot, timeProvider);

        navigator.DeferEscapeReview(BotLevel, current);
        Assert.That(navigator.IsEscapeReviewDeferred(BotLevel, current), Is.True);
        Assert.That(navigator.IsEscapeReviewDeferred(BotLevel + 1, current), Is.False, "a new level opens new maps");
        Assert.That(navigator.IsEscapeReviewDeferred(BotLevel, other), Is.False, "so does a new map");

        timeProvider.UtcNow += TimeSpan.FromMinutes(3);
        Assert.That(navigator.IsEscapeReviewDeferred(BotLevel, current), Is.False, "the verdict is re-checked after a while");
    }

    /// <summary>
    /// The report is about a population, not a single bot: with hundreds of high-level characters spawned
    /// into the weakest maps, none of them may keep travelling between ground of the same strength. Walking
    /// the map choice of such a population step by step must climb the ladder of maps and then stop, never
    /// visit ground it already left, and never spend a fare on a downward trip.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask PopulationOfHighLevelBotsClimbsInsteadOfBouncingAsync()
    {
        var ladder = new[] { 20, 30, 40, 60, 90, 120 };
        foreach (var botLevel in new[] { 150, 200, 250, 300 })
        {
            var gameContext = GameContextTestHelper.CreateGameContext();
            var maps = ladder
                .Select((level, index) => (Level: level, Map: AddHuntingMap(gameContext, (byte)(10 + index), (short)(30200 + level), level)))
                .ToList();
            var bot = await CreateHunterAsync(gameContext, botLevel).ConfigureAwait(false);
            var navigator = new BotNavigator(bot);

            var ground = ladder[0];
            bot.SetCurrentMapSilently(new GameMap(maps[0].Map, TimeSpan.FromMinutes(1), 8));
            var visited = new List<int> { ground };
            for (var step = 0; step < ladder.Length; step++)
            {
                var floor = MapFit.EscapeTargetFloor(ground, mayNotStayAnyway: false);
                var candidates = navigator.GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: floor);
                if (candidates.Count == 0)
                {
                    break;
                }

                var target = candidates.OrderByDescending(c => c.Level).First();
                Assert.That(
                    target.Level,
                    Is.GreaterThan(ground),
                    $"a bot of level {botLevel} on ground of level {ground} picked ground of level {target.Level}");
                ground = target.Level;
                bot.SetCurrentMapSilently(new GameMap(target.Map, TimeSpan.FromMinutes(1), 8));
                visited.Add(ground);
            }

            Assert.That(visited, Is.Ordered, $"a bot of level {botLevel} looped back: {string.Join(" -> ", visited)}");
            Assert.That(visited.Count, Is.LessThanOrEqualTo(ladder.Length), $"the ladder of a bot of level {botLevel} ends in a cycle");
            Assert.That(
                visited[^1],
                Is.EqualTo(ladder[^1]),
                $"a bot of level {botLevel} settled below the best ground it could safely hunt ({string.Join(" -> ", visited)})");
        }
    }

    private static async ValueTask<OfflinePlayer> CreateHunterAsync(IGameContext gameContext, int level = BotLevel)
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        bot.IsAlive = true;
        bot.Money = 100_000;
        bot.Attributes![Stats.Level] = level;

        // Monsters only count when the bot can safely kill them (CombatHandler.IsSafeTarget), which needs
        // attack power; it comes from a learned skill, as the derived damage attributes can't be assigned.
        await bot.SkillList!.AddLearnedSkillAsync(new TestSkill { AttackDamage = 100 }).ConfigureAwait(false);
        return bot;
    }

    /// <summary>
    /// Adds an open map with a single monster of the given level and a warp entry leading into it.
    /// </summary>
    /// <param name="gameContext">The game context to add the map to.</param>
    /// <param name="number">The map number.</param>
    /// <param name="monsterNumber">The number of the monster definition, unique within the process.</param>
    /// <param name="monsterLevel">The monster's level.</param>
    /// <returns>The map.</returns>
    private static GameMapDefinition AddHuntingMap(IGameContext gameContext, byte number, short monsterNumber, int monsterLevel)
    {
        var map = new MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition
        {
            Id = Guid.NewGuid(),
            Number = number,
            TerrainData = new byte[ushort.MaxValue + 3],
        };
        map.MonsterSpawns.Add(new MonsterSpawnArea
        {
            MonsterDefinition = CreateMonsterDefinition(monsterNumber, monsterLevel),
            GameMap = map,
            X1 = 12,
            X2 = 12,
            Y1 = 12,
            Y2 = 12,
            Quantity = 1,
            SpawnTrigger = SpawnTrigger.Automatic,
        });

        var warpGate = CreateGate(map, 10, 10);
        var spawnGate = CreateGate(map, 20, 20);
        spawnGate.IsSpawnGate = true;
        map.ExitGates.Add(spawnGate);
        gameContext.Configuration.Maps.Add(map);
        gameContext.Configuration.WarpList.Add(new MUnique.OpenMU.Persistence.BasicModel.WarpInfo
        {
            Id = Guid.NewGuid(),
            Name = $"Field{number}",
            Gate = warpGate,
            Costs = 0,
        });

        return map;
    }

    private static MonsterDefinition CreateMonsterDefinition(short number, int level)
    {
        var definition = new MonsterDefinition
        {
            Id = Guid.NewGuid(),
            Number = number,
            ObjectKind = NpcObjectKind.Monster,
        };
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.Level, Value = level });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MinimumPhysBaseDmg, Value = 0 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumPhysBaseDmg, Value = 0 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.DefenseBase, Value = 0 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 1 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.AttackRatePvm, Value = 0 });
        return definition;
    }

    private static ExitGate CreateGate(GameMapDefinition map, byte x, byte y)
    {
        return new MUnique.OpenMU.Persistence.BasicModel.ExitGate
        {
            Id = Guid.NewGuid(),
            Map = map,
            X1 = x,
            Y1 = y,
            X2 = (byte)(x + 1),
            Y2 = (byte)(y + 1),
        };
    }

    private sealed class TestSkill : Skill
    {
        public TestSkill()
        {
            this.Requirements = new List<AttributeRequirement>();
            this.ConsumeRequirements = new List<AttributeRequirement>();
            this.Range = 1;
            this.SkillType = SkillType.DirectHit;
            this.DamageType = DamageType.Curse;
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => this.UtcNow;
    }
}