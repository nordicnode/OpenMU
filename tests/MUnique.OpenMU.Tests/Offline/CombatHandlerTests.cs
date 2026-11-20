// <copyright file="CombatHandlerTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Offline;

using System.Threading;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.GameServer.RemoteView.MuHelper;
using MUnique.OpenMU.Pathfinding;
using MonsterDefinition = MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition;
using MonsterAttribute = MUnique.OpenMU.Persistence.BasicModel.MonsterAttribute;

/// <summary>
/// Tests for <see cref="CombatHandler"/>.
/// </summary>
[TestFixture]
public class CombatHandlerTests
{
    private IGameContext _gameContext = null!;
    private readonly Point _origin = new(100, 100);

    /// <summary>
    /// The next monster number to hand out. <see cref="CombatHandler"/> caches a monster's combat stats
    /// statically by its number, so the monsters of these tests must not share one - the default 0 is also
    /// the number of the first monster of the shipped game data, so a test which judges real monsters would
    /// otherwise decide what these tests see.
    /// </summary>
    private static int _nextMonsterNumber = 30500;

    /// <summary>
    /// Sets up a fresh game context before each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
    }

    /// <summary>
    /// Tests that <see cref="CombatHandler.PerformAttackAsync"/> moves closer to the target if out of range.
    /// </summary>
    [Test]
    public async ValueTask PerformAttackAsync_MovesCloserToTargetAsync()
    {
        // Arrange
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        player.Position = this._origin;

        var monster = await this.CreateMonsterAsync(new Point(105, 105)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);

        var config = new MuHelperSettings { HuntingRange = 10 };
        player.HuntingOrigin = this._origin;
        var movementHandler = new MovementHandler(player, config);

        var handler = new CombatHandler(player, config, movementHandler);

        // Act
        // The first call only acquires the target and turns towards it: a fresh target gets a small
        // randomized human-like reaction delay (up to 900 ms) before the bot engages, so we call
        // again after the delay has certainly elapsed.
        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.PerformAttackAsync().ConfigureAwait(false);

        // Assert
        // Should have initiated a walk closer to the monster
        Assert.That(player.IsWalking, Is.True);
    }

    /// <summary>
    /// Tests that <see cref="CombatHandler.PerformAttackAsync"/> does not pick a monster summoned by a
    /// player as its target: it belongs to its owner, and hitting it grants the owner self-defense
    /// against the attacker.
    /// </summary>
    [Test]
    public async ValueTask PerformAttackAsync_IgnoresSummonedMonsterAsync()
    {
        // Arrange
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        player.Position = this._origin;

        var owner = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var summon = await this.CreateMonsterAsync(new Point(105, 105), new SummonedMonsterIntelligence(owner)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(summon).ConfigureAwait(false);

        var config = new MuHelperSettings { HuntingRange = 10 };
        player.HuntingOrigin = this._origin;
        var movementHandler = new MovementHandler(player, config);

        var handler = new CombatHandler(player, config, movementHandler);

        // Act
        // Same timing as in PerformAttackAsync_MovesCloserToTargetAsync, which walks towards a regular
        // monster at the same spot.
        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.PerformAttackAsync().ConfigureAwait(false);

        // Assert
        Assert.That(summon.SummonedBy, Is.SameAs(owner));
        Assert.That(player.IsWalking, Is.False);
        Assert.That(summon.Attributes[Stats.CurrentHealth], Is.EqualTo(100));
    }

    /// <summary>
    /// Tests that <see cref="CombatHandler.PerformDrainLifeRecoveryAsync"/> uses Drain Life when HP is low.
    /// </summary>
    [Test]
    public async ValueTask PerformDrainLifeRecoveryAsync_UsesDrainLifeWhenLowHpAsync()
    {
        // Arrange
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        player.Position = this._origin;
        player.Attributes![Stats.CurrentHealth] = 10; // Low HP
        player.Attributes[Stats.MaximumHealth] = 100;

        // Place monster slightly away from player so GetDirectionTo doesn't return Undefined
        var monsterPosition = new Point((byte)(this._origin.X + 1), this._origin.Y);
        var monster = await this.CreateMonsterAsync(monsterPosition).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);

        var config = new MuHelperSettings
        {
            UseDrainLife = true,
            HealThresholdPercent = 50,
            HuntingRange = 10
        };

        // Add Drain Life skill to player
        var drainSkill = new TestSkill
        {
            Number = 214,
        };
        await player.SkillList!.AddLearnedSkillAsync(drainSkill).ConfigureAwait(false);

        player.HuntingOrigin = this._origin;
        var movementHandler = new MovementHandler(player, config);

        var handler = new CombatHandler(player, config, movementHandler);

        // Act
        await handler.PerformDrainLifeRecoveryAsync().ConfigureAwait(false);

        // Assert
        // We verify that rotation was updated, which happens during ExecuteAttackAsync
        Assert.That(player.Rotation, Is.Not.EqualTo(default(Direction)));
    }

    /// <summary>
    /// Tests that <see cref="CombatHandler.IsSafeTarget"/> lets a fresh level-1 character engage a
    /// monster a few levels above it: without the level headroom every monster is above the fresh
    /// bot's effective level, so it could never acquire a target and just wandered.
    /// </summary>
    [Test]
    public async ValueTask IsSafeTarget_LevelOnePlayer_MayEngageLevelSixMonster()
    {
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        player.Attributes![Stats.Level] = 1;
        await player.SkillList!.AddLearnedSkillAsync(new TestSkill { AttackDamage = 20 }).ConfigureAwait(false);

        var monster = await this.CreateMonsterAsync(default, 6, 2, 2, 5, 300, 28).ConfigureAwait(false);

        Assert.That(CombatHandler.IsSafeTarget(player, monster.Definition), Is.True);
    }

    /// <summary>
    /// Tests that the level headroom of <see cref="CombatHandler.IsSafeTarget"/> does not open the
    /// floodgates: a level-1 character still rejects a monster far above its tier.
    /// </summary>
    [Test]
    public async ValueTask IsSafeTarget_LevelOnePlayer_RejectsLevelThirtyMonster()
    {
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        player.Attributes![Stats.Level] = 1;

        var monster = await this.CreateMonsterAsync(default, 30, 2, 2, 5, 300, 28).ConfigureAwait(false);

        Assert.That(CombatHandler.IsSafeTarget(player, monster.Definition), Is.False);
    }

    /// <summary>
    /// Bots defer drops while injured, but a player's offline helper keeps its normal pickup behavior.
    /// </summary>
    [TestCase(true, 59, true)]
    [TestCase(true, 60, false)]
    [TestCase(false, 30, false)]
    public async ValueTask LootDeferralRespectsHealthAndSessionTypeAsync(bool isBot, int healthPercent, bool expected)
    {
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        player.Account!.IsBot = isBot;
        player.Attributes![Stats.CurrentHealth] = player.Attributes[Stats.MaximumHealth] * healthPercent / 100;
        var config = new MuHelperSettings { PotionThresholdPercent = 60 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        Assert.That(handler.ShouldDeferLoot, Is.EqualTo(expected));
    }

    /// <summary>
    /// A legal aggressor takes priority over loot, but an expired self-defense window does not.
    /// </summary>
    [Test]
    public async ValueTask LootDeferralRequiresLegalNearbyAggressorAsync()
    {
        var bot = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        bot.Account!.IsBot = true;
        bot.Position = this._origin;
        var attacker = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        attacker.Position = new Point(101, 100);
        attacker.IsAlive = true;
        bot.RegisterAggressor(attacker);
        var config = new MuHelperSettings { UseSelfDefense = true, HuntingRange = 6 };
        var handler = new CombatHandler(bot, config, new MovementHandler(bot, config));

        Assert.That(handler.ShouldDeferLoot, Is.False);
        this._gameContext.SelfDefenseState[(attacker, bot)] = DateTime.UtcNow.AddMinutes(1);
        Assert.That(handler.ShouldDeferLoot, Is.True);
        attacker.Position = new Point(200, 200);
        Assert.That(handler.ShouldDeferLoot, Is.False, "a distant aggressor must not block looting");
        attacker.Position = new Point(101, 100);
        this._gameContext.SelfDefenseState[(attacker, bot)] = DateTime.UtcNow;
        Assert.That(handler.ShouldDeferLoot, Is.False);
    }

    /// <summary>A bot engaged in an active invasion attempt defers loot until its target is gone.</summary>
    [Test]
    public async ValueTask BossAttemptDefersLootOnlyDuringActiveFightAsync()
    {
        var bot = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        bot.Account!.IsBot = true;
        bot.Position = this._origin;
        bot.HuntingOrigin = this._origin;
        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await bot.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(bot, config, new MovementHandler(bot, config));
        bot.BossAttempt = new BotBossAttempt(bot.CurrentMap.Definition, DateTime.UtcNow.AddMinutes(1));

        Assert.That(handler.ShouldDeferLoot, Is.False, "traveling to an invasion is not an active fight");
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(handler.ShouldDeferLoot, Is.True);
        bot.BossAttempt = new BotBossAttempt(bot.CurrentMap.Definition, DateTime.UtcNow.AddMinutes(-1));
        Assert.That(handler.ShouldDeferLoot, Is.False);
    }

    /// <summary>Automatic selection requires an unlocked combo and a complete learned path.</summary>
    [Test]
    public async ValueTask BotComboSelectionRequiresUnlockAndCompletePathAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        Assert.That(handler.SelectBotComboSkill(), Is.Null, "the quest reward must unlock combos");
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        await player.SkillList!.AddLearnedSkillAsync(skills[0]).ConfigureAwait(false);
        await player.SkillList.AddLearnedSkillAsync(skills[1]).ConfigureAwait(false);
        Assert.That(handler.SelectBotComboSkill(), Is.Null, "a chain without a finisher must not start");
        await player.SkillList.AddLearnedSkillAsync(skills[2]).ConfigureAwait(false);
        Assert.That(handler.SelectBotComboSkill()?.Skill, Is.SameAs(skills[0]));
        player.Account!.IsBot = false;
        Assert.That(handler.SelectBotComboSkill(), Is.Null, "human helpers must not auto-select combos");
    }

    /// <summary>A quest reward activates the combo in the live attribute graph without a relog.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask QuestComboRewardActivatesLiveAttributesAsync(bool existingComposable)
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills, includeComboAttribute: false).ConfigureAwait(false);
        if (existingComposable)
        {
            player.Attributes!.GetOrCreateAttribute(Stats.IsSkillComboAvailable);
        }

        var quest = new Persistence.BasicModel.QuestDefinition { Group = 0, Number = 3 };
        quest.Rewards.Add(new Persistence.BasicModel.QuestReward
        {
            RewardType = DataModel.Configuration.Quests.QuestRewardType.Attribute,
            AttributeReward = Stats.IsSkillComboAvailable,
            Value = 1,
        });
        var state = new Persistence.BasicModel.CharacterQuestState { Group = 0, ActiveQuest = quest };
        Mock.Get(player.SelectedCharacter!).Setup(c => c.QuestStates).Returns(new List<CharacterQuestState> { state });
        Assert.That(player.ComboState, Is.Null);

        await new QuestCompletionAction().CompleteQuestAsync(player, 0, 3).ConfigureAwait(false);

        Assert.That(state.LastFinishedQuest, Is.SameAs(quest));
        Assert.That(player.SelectedCharacter!.Attributes.Single(a => a.Definition == Stats.IsSkillComboAvailable).Value, Is.EqualTo(1));
        Assert.That(player.Attributes![Stats.IsSkillComboAvailable], Is.EqualTo(1));
        Assert.That(player.ComboState, Is.Not.Null);
    }

    /// <summary>A combo definition gained after entering the world becomes available without a relog.</summary>
    [Test]
    public async ValueTask BotComboRecognizesClassChangeAfterLoginAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills, configureAfterLogin: true).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        Assert.That(handler.SelectBotComboSkill()?.Skill, Is.SameAs(skills[0]));
    }

    /// <summary>Targeted and area steps advance the configured combo and consume their costs.</summary>
    [Test]
    public async ValueTask BotComboAttacksAdvanceAndConsumeResourcesAsync()
    {
        var skills = CreateComboSkills();
        skills[1].SkillType = SkillType.AreaSkillAutomaticHits;
        skills[1].ConsumeRequirements.Add(new AttributeRequirement { Attribute = Stats.CurrentMana, MinimumValue = 5 });
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        player.Attributes[Stats.CurrentMana] = 30;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        var combo = player.ComboState!;

        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState));
        Assert.That(handler.HasActiveBotCombo, Is.True);
        Assert.That(handler.SkillCooldownTicks, Is.GreaterThan(0), "combo attacks need a human-paced gap");
        Assert.That(handler.SelectBotComboSkill()?.Skill, Is.SameAs(skills[1]));
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.CurrentMana], Is.EqualTo(25));
        Assert.That(handler.SelectBotComboSkill()?.Skill, Is.SameAs(skills[2]));
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        Assert.That(combo.CurrentState, Is.EqualTo(combo.InitialState));
        Assert.That(handler.HasActiveBotCombo, Is.False);
        Assert.That(handler.SkillCooldownTicks, Is.GreaterThan(0), "a completed combo has a recovery delay");
    }

    /// <summary>The normal bot combat loop selects and completes the configured chain without helper slots.</summary>
    [Test]
    public async ValueTask BotCombatLoopUsesAutomaticComboAsync()
    {
        var skills = CreateComboSkills();
        skills[1].SkillType = SkillType.AreaSkillAutomaticHits;
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        var combo = player.ComboState!;

        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState));
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState));
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(combo.CurrentState, Is.EqualTo(combo.InitialState));
    }

    /// <summary>Rejected area attacks do not consume resources or advance the combo.</summary>
    [Test]
    public async ValueTask BotComboRejectedAreaAttackResetsWithoutConsumptionAsync()
    {
        var skills = CreateComboSkills();
        skills[1].SkillType = SkillType.AreaSkillAutomaticHits;
        skills[1].ConsumeRequirements.Add(new AttributeRequirement { Attribute = Stats.CurrentMana, MinimumValue = 5 });
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        player.Attributes[Stats.CurrentMana] = 30;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        var second = handler.SelectBotComboSkill()!;
        player.Attributes[Stats.IsStunned] = 1;
        await handler.ExecuteBotComboAttackAsync(monster, second).ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.CurrentMana], Is.EqualTo(30));
        Assert.That(player.ComboState!.CurrentState, Is.EqualTo(player.ComboState.InitialState));
        Assert.That(handler.HasActiveBotCombo, Is.False);
    }

    /// <summary>A target which moves out of range aborts the chain instead of spending the next skill.</summary>
    [Test]
    public async ValueTask BotComboAbortsWhenTargetMovesOutOfRangeAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        var next = handler.SelectBotComboSkill()!;
        monster.Position = new Point(104, 100);
        await handler.ExecuteBotComboAttackAsync(monster, next).ConfigureAwait(false);

        Assert.That(player.ComboState!.CurrentState, Is.EqualTo(player.ComboState.InitialState));
        Assert.That(handler.HasActiveBotCombo, Is.False);
        Assert.That(handler.SelectBotComboSkill()?.Skill, Is.SameAs(skills[0]));
    }

    /// <summary>An expired attempt no longer prevents other helper activities.</summary>
    [Test]
    public async ValueTask BotComboExpiresAtConfiguredDeadlineAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills, TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);

        Assert.That(handler.HasActiveBotCombo, Is.False);
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(player.ComboState!.CurrentState, Is.EqualTo(player.ComboState.InitialState));
    }

    /// <summary>
    /// A target which steps out of reach costs the bot a step, not the whole chain: it walks after it
    /// while the completion window is still open.
    /// </summary>
    [Test]
    public async ValueTask BotComboHoldsWhileTheTargetIsOutOfReachAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        // Acquire the target first: a fresh target costs the bot its human-like reaction delay, so the
        // first call only turns towards it and the chain still starts at its first step.
        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        var combo = player.ComboState!;
        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState));

        monster.Position = new Point(104, 100);
        await handler.PerformAttackAsync().ConfigureAwait(false);

        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState), "walking after the target keeps the chain");
        Assert.That(handler.HasActiveBotCombo, Is.True);
        Assert.That(player.IsWalking, Is.True);
    }

    /// <summary>
    /// A combo step which is momentarily unaffordable waits for the recovery of the next tick instead
    /// of dropping the chain - and only an expired attempt falls back to a normal attack.
    /// </summary>
    [Test]
    public async ValueTask BotComboWaitsForResourcesAsync()
    {
        var skills = CreateComboSkills();
        skills[1].ConsumeRequirements.Add(new AttributeRequirement { Attribute = Stats.CurrentMana, MinimumValue = 5 });
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        player.Attributes[Stats.CurrentMana] = 30;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        var combo = player.ComboState!;
        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState));

        player.Attributes[Stats.CurrentMana] = 0;
        Assert.That(player.Attributes[Stats.CurrentMana], Is.EqualTo(0), "the mana bar is empty");
        Assert.That(handler.SelectBotComboSkill(), Is.Null, "the next step needs mana");
        await handler.PerformAttackAsync().ConfigureAwait(false);

        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState), "the chain waits for the mana potion");
        Assert.That(handler.HasActiveBotCombo, Is.True);
    }

    private static TestSkill[] CreateComboSkills()
    {
        return
        [
            new TestSkill { Number = 700, Target = SkillTarget.Explicit },
            new TestSkill { Number = 701, Target = SkillTarget.Explicit },
            new TestSkill { Number = 702, Target = SkillTarget.Explicit },
        ];
    }

    /// <summary>
    /// A running duel makes the bot fight its partner instead of the monsters around it. The duel
    /// partner is a player, which the bot otherwise only attacks in self-defense or in an event.
    /// </summary>
    [Test]
    public async ValueTask BotFightsItsDuelOpponentAsync()
    {
        var player = await this.CreateComboBotAsync(CreateComboSkills()).ConfigureAwait(false);
        var opponent = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        opponent.IsAlive = true;
        opponent.Position = new Point((byte)(this._origin.X + 2), this._origin.Y);

        var monster = await this.CreateMonsterAsync(new Point((byte)(this._origin.X + 1), (byte)(this._origin.Y + 2))).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);

        var room = new DuelRoom(new Mock<DuelArea>().Object, opponent, player) { State = DuelState.DuelStarted };
        player.DuelRoom = room;
        opponent.DuelRoom = room;

        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        await handler.PerformAttackAsync().ConfigureAwait(false);

        Assert.That(handler.HasPlayerOpponent, Is.True, "the duel partner outranks the monster next to it");
    }

    /// <summary>
    /// A duel request which the bot hasn't answered yet is no fight: the requester must not replace the
    /// monster the bot is hunting, or it stops fighting for every request it receives.
    /// </summary>
    [Test]
    public async ValueTask PendingDuelRequestDoesNotStealTheTargetAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);

        var requester = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        requester.IsAlive = true;
        requester.Position = new Point(104, 100);

        var room = new DuelRoom(new Mock<DuelArea>().Object, requester, player) { State = DuelState.DuelRequested };
        player.DuelRoom = room;
        requester.DuelRoom = room;

        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        var combo = player.ComboState!;

        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.PerformAttackAsync().ConfigureAwait(false);

        Assert.That(combo.CurrentState, Is.Not.EqualTo(combo.InitialState), "the bot goes on hunting its monster");
    }

    /// <summary>
    /// A dead duel partner (between the rounds) is waited for, not chased: nothing may be struck through
    /// its corpse, and the bot must not walk after it either.
    /// </summary>
    [Test]
    public async ValueTask DeadDuelPartnerIsNoTargetAsync()
    {
        var skills = CreateComboSkills();
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var opponent = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        opponent.IsAlive = false;
        opponent.Position = new Point(104, 100);

        var room = new DuelRoom(new Mock<DuelArea>().Object, opponent, player) { State = DuelState.DuelStarted };
        player.DuelRoom = room;
        opponent.DuelRoom = room;

        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        await handler.PerformAttackAsync().ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        await handler.PerformAttackAsync().ConfigureAwait(false);

        Assert.That(player.IsWalking, Is.False, "the bot waits for the respawn instead of walking after the corpse");
        Assert.That(handler.HasPlayerOpponent, Is.False);
    }

    /// <summary>Without a duel, the same bot leaves the player alone and hunts the monster.</summary>
    [Test]
    public async ValueTask BotWithoutADuelLeavesAPlayerAloneAsync()
    {
        var player = await this.CreateComboBotAsync(CreateComboSkills()).ConfigureAwait(false);
        var bystander = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        bystander.IsAlive = true;
        bystander.Position = new Point((byte)(this._origin.X + 2), this._origin.Y);

        var monster = await this.CreateMonsterAsync(new Point((byte)(this._origin.X + 1), (byte)(this._origin.Y + 2))).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);

        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));

        await handler.PerformAttackAsync().ConfigureAwait(false);

        Assert.That(handler.HasPlayerOpponent, Is.False);
    }

    /// <summary>
    /// The blade knight combo is finished with Death Stab, a direct hit which also hits everything
    /// around its target. The chain has to accept that target kind, or the bot stops one step short
    /// of its own finisher.
    /// </summary>
    [Test]
    public async ValueTask BotComboAcceptsAnImplicitSplashFinisherAsync()
    {
        var skills = CreateComboSkills();
        skills[2].Target = SkillTarget.ExplicitWithImplicitInRange;
        skills[2].ImplicitTargetRange = 2;
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        player.Attributes![Stats.IsSkillComboAvailable] = 1;
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        var combo = player.ComboState!;

        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        Assert.That(handler.SelectBotComboSkill()?.Skill, Is.SameAs(skills[2]), "the finisher must be reachable");

        await handler.ExecuteBotComboAttackAsync(monster, handler.SelectBotComboSkill()!).ConfigureAwait(false);
        Assert.That(combo.CurrentState, Is.EqualTo(combo.InitialState), "the chain completed");
    }

    /// <summary>
    /// A caster-centred skill hits everything around the bot, players included - it is left unused
    /// while a bystander who isn't a legal PvP target stands in that reach, even though no explicit
    /// target takes part in the decision.
    /// </summary>
    [Test]
    public async ValueTask BotDoesNotSplashAnIllegalBystanderWithACasterCentredSkillAsync()
    {
        var skills = CreateComboSkills();
        skills[2].Target = SkillTarget.ImplicitAllInRange;
        skills[2].ImplicitTargetRange = 3;
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(handler.WouldHitIllegalPlayer(skills[2]), Is.False, "nobody else is around the bot");

        // The game's AreaSkillHitsPlayer setting doesn't matter here - the skill hits players anyway.
        var bystander = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        bystander.IsAlive = true;
        bystander.SelectedCharacter!.PositionX = (byte)(this._origin.X + 2);
        bystander.SelectedCharacter.PositionY = (byte)(this._origin.Y + 1);
        await player.CurrentMap!.AddAsync(bystander).ConfigureAwait(false);

        Assert.That(handler.WouldHitIllegalPlayer(skills[2]), Is.True, "the skill would hit the bystander");
    }

    /// <summary>
    /// A skill which splashes around its target is left unused while a player stands near the target
    /// who is not a legal PvP target - the splash would make the bot an outlaw otherwise.
    /// </summary>
    [Test]
    public async ValueTask BotDoesNotSplashAnIllegalBystanderAsync()
    {
        var skills = CreateComboSkills();
        skills[2].Target = SkillTarget.ExplicitWithImplicitInRange;
        skills[2].ImplicitTargetRange = 2;
        var player = await this.CreateComboBotAsync(skills).ConfigureAwait(false);
        foreach (var skill in skills)
        {
            await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);
        }

        var monster = await this.CreateMonsterAsync(new Point(101, 100)).ConfigureAwait(false);
        await player.CurrentMap!.AddAsync(monster).ConfigureAwait(false);
        var config = new MuHelperSettings { HuntingRange = 6 };
        var handler = new CombatHandler(player, config, new MovementHandler(player, config));
        await handler.PerformAttackAsync().ConfigureAwait(false);
        Assert.That(handler.WouldHitIllegalPlayer(skills[2]), Is.False, "nobody else stands next to the monster");

        // The game passes players to the splash only with this setting - and then a bystander counts.
        this._gameContext.Configuration.AreaSkillHitsPlayer = true;
        var bystander = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        bystander.IsAlive = true;
        bystander.SelectedCharacter!.PositionX = (byte)(this._origin.X + 2);
        bystander.SelectedCharacter.PositionY = (byte)(this._origin.Y - 1);
        await player.CurrentMap!.AddAsync(bystander).ConfigureAwait(false);

        Assert.That(handler.WouldHitIllegalPlayer(skills[2]), Is.True, "the splash would hit the bystander");
    }

    private async ValueTask<OfflinePlayer> CreateComboBotAsync(TestSkill[] skills, TimeSpan? maximumCompletionTime = null, bool configureAfterLogin = false, bool includeComboAttribute = true)
    {
        var definition = new TestComboDefinition { MaximumCompletionTime = maximumCompletionTime ?? TimeSpan.FromSeconds(3) };
        for (var i = 0; i < skills.Length; i++)
        {
            definition.Steps.Add(new SkillComboStep { Order = i + 1, Skill = skills[i], IsFinalStep = i == skills.Length - 1 });
        }

        var character = PlayerTestHelper.CreateCharacter(this._gameContext);
        if (includeComboAttribute)
        {
            character.Attributes.Add(new StatAttribute(Stats.IsSkillComboAvailable, 0));
        }

        character.Attributes.Add(new StatAttribute(Stats.IsStunned, 0));
        Mock.Get(character.CharacterClass!).SetupProperty(c => c.ComboDefinition, configureAfterLogin ? null : definition);
        var player = new OfflinePlayer(this._gameContext);
        await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
        if (configureAfterLogin)
        {
            character.CharacterClass!.ComboDefinition = definition;
        }

        player.Account!.IsBot = true;
        player.IsAlive = true;
        player.Position = this._origin;
        player.HuntingOrigin = this._origin;
        return player;
    }

    private async ValueTask<OfflinePlayer> CreateOfflinePlayerAsync()
    {
        return await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
    }

    private async ValueTask<Monster> CreateMonsterAsync(Point position, INpcIntelligence? intelligence = null)
    {
        return await this.CreateMonsterAsync(position, 6, minDamage: 2, maxDamage: 2, defense: 5, health: 300, attackRate: 28, intelligence).ConfigureAwait(false);
    }

    private async ValueTask<Monster> CreateMonsterAsync(Point position, int level, float minDamage, float maxDamage, float defense, float health, float attackRate, INpcIntelligence? intelligence = null)
    {
        var monsterDefinition = new MonsterDefinition
        {
            Id = Guid.NewGuid(),
            Number = (short)Interlocked.Increment(ref _nextMonsterNumber),
            ObjectKind = NpcObjectKind.Monster,
        };
        monsterDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.Level, Value = level });
        monsterDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MinimumPhysBaseDmg, Value = minDamage });
        monsterDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumPhysBaseDmg, Value = maxDamage });
        monsterDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.DefenseBase, Value = defense });
        monsterDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = health });
        monsterDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.AttackRatePvm, Value = attackRate });

        var map = await this._gameContext.GetMapAsync(0).ConfigureAwait(false)!;
        var spawnArea = new MonsterSpawnArea
        {
            MonsterDefinition = monsterDefinition,
            GameMap = map!.Definition,
            X1 = position.X,
            Y1 = position.Y,
            X2 = position.X,
            Y2 = position.Y,
            Quantity = 1,
        };

        var monster = new Monster(
            spawnArea,
            monsterDefinition,
            map,
            NullDropGenerator.Instance,
            intelligence ?? new Mock<INpcIntelligence>().Object,
            this._gameContext.PlugInManager,
            this._gameContext.PathFinderPool);

        monster.Initialize();
        monster.Attributes[Stats.CurrentHealth] = 100;

        return monster;
    }

    private sealed class TestComboDefinition : SkillComboDefinition
    {
        public TestComboDefinition()
        {
            this.Steps = new List<SkillComboStep>();
        }
    }

    private class TestSkill : Skill
    {
        public TestSkill()
        {
            this.Requirements = new List<AttributeRequirement>();
            this.ConsumeRequirements = new List<AttributeRequirement>();
            this.AttributeRelationships = new List<AttributeRelationship>();
            this.Range = 1;
            this.SkillType = SkillType.DirectHit;
            this.DamageType = DamageType.Curse;
        }
    }
}
