// <copyright file="BotRealMapChoiceTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using NUnit.Framework;

/// <summary>
/// The map choice of a high-level bot, judged against the real Season Six configuration: which maps does a
/// level 250 character which stands on Lorencia actually get offered (issue #1542).
/// </summary>
/// <remarks>
/// The other map tests state their own small configuration, so they hold regardless of the game data. This
/// one deliberately reads the shipped one: the reported bug was a bug of the real map band - Lorencia's
/// monsters are the strongest a weakly geared high-level bot could handle, while Noria (18), Dungeon (19)
/// or Elvenland (24) offered ground of the same strength, so "anything safe beats staying" kept sending it
/// to the next starter map. It also documents what the fix changes, by comparing against that old rule.
/// </remarks>
[TestFixture]
public class BotRealMapChoiceTest
{
    /// <summary>
    /// The starter band of the report: Lorencia, Devias, Noria and Elvenland. A bot which leaves trivial
    /// ground must not be sent into any of them - they are the band it has to leave.
    /// </summary>
    private static readonly short[] StarterBandMaps = [0, 2, 3, 51];

    private const ushort Lorencia = 0;

    /// <summary>
    /// What the fix is for, on the real maps: a level 250 character which starts on Lorencia moves to
    /// strictly better ground with every trip and ends up on real hunting ground - Tarkan, Aida, Atlans,
    /// Vulcanus - instead of staying in the starter band. Even the unluckiest draw does that, so the
    /// randomized map choice can't reintroduce the loop of the report.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask HighLevelBotClimbsOutOfTheStarterBandAsync()
    {
        var (gameContext, bot) = await CreateBotOnLorenciaAsync(vitality: 2000, attackPower: 600).ConfigureAwait(false);
        var navigator = new BotNavigator(bot);

        var ground = GroundLevel(bot, bot.CurrentMap!.Definition);
        var visited = new List<(string Map, int Level)> { (bot.CurrentMap!.Definition.Name, ground) };
        for (var hop = 0; hop < 12; hop++)
        {
            var floor = MapFit.EscapeTargetFloor(ground, mayNotStayAnyway: false);
            var candidates = navigator.GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: floor);
            if (candidates.Count == 0)
            {
                break;
            }

            // The worst pick the draw allows: whatever is left over from the ranking.
            var target = candidates.OrderBy(c => c.Level).First();
            Assert.That(
                target.Level,
                Is.GreaterThan(ground),
                $"the bot was offered ground of level {target.Level} while standing on ground of level {ground}");
            ground = target.Level;
            bot.SetCurrentMapSilently(new GameMap(target.Map, TimeSpan.FromMinutes(1), 8));
            visited.Add((target.Map.Name, ground));
        }

        var trail = string.Join(" -> ", visited.Select(v => $"{v.Map}({v.Level})"));
        Assert.That(visited.Select(v => v.Level), Is.Ordered, $"the bot looped: {trail}");
        Assert.That(visited, Has.Count.LessThanOrEqualTo(12), $"the climb does not end: {trail}");
        Assert.That(ground, Is.GreaterThanOrEqualTo(70), $"the bot settled on ground too weak for its gear: {trail}");
        Assert.That(
            visited.Skip(1).Select(v => v.Level),
            Has.None.LessThanOrEqualTo(GroundLevel(bot, gameContext.Configuration.Maps.First(m => m.Number == Lorencia))),
            $"the bot never moved to ground as weak as Lorencia's: {trail}");
    }

    /// <summary>
    /// The counter-check, on the real data: with the old rule - any safe map beats staying - the same bot is
    /// offered Noria, Dungeon, Devias and Elvenland, i.e. ground of the strength it already stands on, and
    /// the draw sent it to one of them on every cooldown. That is the loop from the report; the floor of
    /// <see cref="MapFit.EscapeTargetFloor"/> ends it.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask WithoutTheFloorTheStarterBandWouldBeOfferedAgainAsync()
    {
        var (gameContext, bot) = await CreateBotOnLorenciaAsync(vitality: 2000, attackPower: 600).ConfigureAwait(false);
        var ground = GroundLevel(bot, bot.CurrentMap!.Definition);
        var navigator = new BotNavigator(bot);

        var withoutFloor = navigator.GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: 1);
        var withFloor = navigator.GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: MapFit.EscapeTargetFloor(ground, mayNotStayAnyway: false));

        Assert.That(
            withoutFloor.Select(c => c.Map.Number).Intersect(StarterBandMaps),
            Is.Not.Empty,
            "the old rule did offer the starter band, which is what the report saw");
        Assert.That(
            withoutFloor.Select(c => c.Level),
            Has.Some.LessThanOrEqualTo(ground),
            "including ground no better than the ground the bot stood on");
        Assert.That(
            withFloor.Select(c => c.Level),
            Has.None.LessThanOrEqualTo(ground),
            "the floor keeps those out");

        _ = gameContext;
    }

    /// <summary>
    /// A bot whose gear is weak for its level - the case from the report - is not left without a target, and
    /// is not offered the weaker starter maps either. It gets the single best ground it can survive, makes
    /// that one step, and then farms there (see <see cref="BotMapFitTest"/> for the settling).
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask WeakGearStillClimbsAndKeepsOutOfTheWeakerStarterMapsAsync()
    {
        var (gameContext, bot) = await CreateBotOnLorenciaAsync(vitality: 0, attackPower: 100).ConfigureAwait(false);
        var ground = GroundLevel(bot, bot.CurrentMap!.Definition);
        var floor = MapFit.EscapeTargetFloor(ground, mayNotStayAnyway: false);

        var candidates = new BotNavigator(bot).GetBetterMapCandidates(escape: true, minimumMonsterLevel: 0, escapeFloor: floor);

        Assert.That(candidates, Is.Not.Empty, "there is still ground better than Lorencia which this bot can survive");
        Assert.That(
            candidates.Select(c => c.Level),
            Has.All.GreaterThan(ground),
            "no Noria (18) or Dungeon (19) next to a Lorencia (19) - those were the bounces");

        _ = gameContext;
    }

    /// <summary>
    /// Creates a level 250 bot which stands on Lorencia, in a game context with the real Season Six data.
    /// </summary>
    /// <param name="vitality">
    /// The vitality stat, which is what a real character's gear and points add up to - it drives the health
    /// the safety check weighs a monster's damage against. 0 is the weak-geared case of the report.
    /// </param>
    /// <param name="attackPower">
    /// The attack damage of a learned skill; the damage attributes themselves are derived and can't be set.
    /// </param>
    /// <returns>The game context and the bot.</returns>
    private static async ValueTask<(IGameContext GameContext, OfflinePlayer Bot)> CreateBotOnLorenciaAsync(int vitality, int attackPower)
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        await new MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory())
            .CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var configurationContext = contextProvider.CreateNewContext();
        var gameConfiguration = (await configurationContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();

        var mapInitializer = new MapInitializer(gameConfiguration, new NullLogger<MapInitializer>(), NullDropGenerator.Instance, null);
        var gameContext = new GameContext(
            gameConfiguration,
            contextProvider,
            mapInitializer,
            new NullLoggerFactory(),
            new PlugInManager(null, new NullLoggerFactory(), null, null),
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());
        mapInitializer.PlugInManager = gameContext.PlugInManager;
        mapInitializer.PathFinderPool = gameContext.PathFinderPool;

        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        bot.IsAlive = true;
        bot.Money = 100_000;
        bot.Attributes![Stats.Level] = 250;
        bot.Attributes[Stats.BaseVitality] = vitality;
        await bot.SkillList!.AddLearnedSkillAsync(new TestSkill { AttackDamage = attackPower }).ConfigureAwait(false);
        bot.SetCurrentMapSilently(await gameContext.GetMapAsync(Lorencia).ConfigureAwait(false));
        bot.Position = bot.CurrentMap!.Definition.ExitGates.First(gate => gate.IsSpawnGate).GetRandomPoint();
        return (gameContext, bot);
    }

    /// <summary>
    /// Gets the level of the strongest monster of a map the bot can safely fight - the same ground level the
    /// navigator works with (see <see cref="CombatHandler.IsSafeTarget"/>).
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="map">The map.</param>
    /// <returns>The ground level of the map.</returns>
    private static int GroundLevel(Player bot, GameMapDefinition map)
        => map.MonsterSpawns
            .Where(area => area is { Quantity: > 0, SpawnTrigger: SpawnTrigger.Automatic, MonsterDefinition.ObjectKind: NpcObjectKind.Monster })
            .Where(area => CombatHandler.IsSafeTarget(bot, area.MonsterDefinition!))
            .Select(area => (int)(area.MonsterDefinition!.Attributes.FirstOrDefault(a => a.AttributeDefinition == Stats.Level)?.Value ?? 0))
            .DefaultIfEmpty(0)
            .Max();

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
}