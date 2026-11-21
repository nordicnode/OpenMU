// <copyright file="BotQuestHandlerTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using NUnit.Framework;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests the decisions of <see cref="BotQuestHandler"/>: which quest a bot runs to unlock its combo, how it
/// reads the quest's progress and what it has to farm. None of these need a live world - the handler is fed
/// from the configuration and the character's quest state.
/// </summary>
[TestFixture]
public class BotQuestHandlerTest
{
    private const short QuestGroup = 5;

    private IGameContext _gameContext = null!;
    private Player _player = null!;
    private BasicModel.MonsterDefinition _giver = null!;
    private BasicModel.QuestDefinition _heroStatus = null!;
    private BasicModel.QuestDefinition _darkStone = null!;

    /// <summary>
    /// Builds a giver with the two quests of the Marlon chain: a first quest without a combo reward and the
    /// second one which unlocks the combo.
    /// </summary>
    [SetUp]
    public async ValueTask SetUpAsync()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
        this._player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        Mock.Get(this._player.SelectedCharacter!).Setup(c => c.QuestStates).Returns(new List<CharacterQuestState>());

        this._giver = new BasicModel.MonsterDefinition { Number = 229, NpcWindow = NpcWindow.LegacyQuest };
        this._heroStatus = this.CreateQuest(1, rewardsCombo: false);
        this._darkStone = this.CreateQuest(2, rewardsCombo: true);
        this._giver.Quests.Add(this._heroStatus);
        this._giver.Quests.Add(this._darkStone);
        this._gameContext.Configuration.Monsters.Add(this._giver);
    }

    /// <summary>
    /// The bot walks the chain in order: the first quest while nothing is finished, the second one after
    /// the first was handed in, and nothing at all once the combo is unlocked.
    /// </summary>
    [Test]
    public void PicksTheNextUnfinishedQuestOfTheComboChain()
    {
        var goal = BotQuestHandler.GetComboQuestGoal(this._player);

        Assert.That(goal, Is.Not.Null);
        Assert.That(goal!.Quest, Is.SameAs(this._heroStatus), "the lowest unfinished quest comes first");
        Assert.That(goal.Giver, Is.SameAs(this._giver));

        this.SetQuestState(new BasicModel.CharacterQuestState { Group = QuestGroup, LastFinishedQuest = this._heroStatus });
        goal = BotQuestHandler.GetComboQuestGoal(this._player);
        Assert.That(goal?.Quest, Is.SameAs(this._darkStone), "the quest which unlocks the combo is next");

        this.SetQuestState(new BasicModel.CharacterQuestState { Group = QuestGroup, LastFinishedQuest = this._darkStone });
        Assert.That(BotQuestHandler.GetComboQuestGoal(this._player), Is.Null, "a finished chain is no goal anymore");
    }

    /// <summary>
    /// A chain which ends at one giver does not end the search: when another giver continues the group
    /// with higher quest numbers, the bot runs that one instead of believing the combo chain is done.
    /// </summary>
    [Test]
    public void ContinuesTheChainAtAnotherGiver()
    {
        var secondGiver = this.AddSecondGiver();
        this.SetQuestState(new BasicModel.CharacterQuestState { Group = QuestGroup, LastFinishedQuest = this._darkStone });

        var goal = BotQuestHandler.GetComboQuestGoal(this._player);

        Assert.That(goal, Is.Not.Null, "the end of one giver's chain is not the end of the combo chain");
        Assert.That(goal!.Giver, Is.SameAs(secondGiver));
        Assert.That(goal.Quest.Number, Is.EqualTo(3), "the next unfinished quest of the group is picked");
    }

    /// <summary>
    /// An active quest which belongs to another giver's part of the group is still the bot's active
    /// quest - it finishes that one instead of reporting that it has nothing to do.
    /// </summary>
    [Test]
    public void KeepsTheActiveQuestOfAnotherGiver()
    {
        var secondGiver = this.AddSecondGiver();
        var active = secondGiver.Quests.First(quest => quest.Number == 3);
        this.SetQuestState(new BasicModel.CharacterQuestState { Group = QuestGroup, ActiveQuest = active });

        var goal = BotQuestHandler.GetComboQuestGoal(this._player);

        Assert.That(goal, Is.Not.Null);
        Assert.That(goal!.Giver, Is.SameAs(secondGiver));
        Assert.That(goal.Quest, Is.SameAs(active));
    }

    /// <summary>
    /// A bot which already has the combo - for example because a game master granted it - does not run the
    /// chain. The same holds for a server whose giver offers no combo quest at all.
    /// </summary>
    [Test]
    public void HasNoGoalWhenTheComboIsAlreadyUnlocked()
    {
        this._player.Attributes!.TryAddStatAttribute(new StatAttribute(Stats.IsSkillComboAvailable, 1));

        Assert.That(BotQuestHandler.GetComboQuestGoal(this._player), Is.Null);

        this._player.Attributes[Stats.IsSkillComboAvailable] = 0;
        this._gameContext.Configuration.Monsters.Clear();
        Assert.That(BotQuestHandler.GetComboQuestGoal(this._player), Is.Null, "no giver, no goal");
    }

    /// <summary>
    /// The progress follows the character's quest state: not started, in progress while a requirement is
    /// open, ready to hand in once every requirement is met, and finished afterwards.
    /// </summary>
    [Test]
    public void ReadsTheProgressOfTheActiveQuest()
    {
        Assert.That(BotQuestHandler.GetProgress(this._player, this._heroStatus), Is.EqualTo(BotQuestHandler.QuestProgress.NotStarted));

        var killRequirement = new BasicModel.QuestMonsterKillRequirement { Monster = new BasicModel.MonsterDefinition { Number = 1 }, MinimumNumber = 3 };
        this._heroStatus.RequiredMonsterKills.Add(killRequirement);
        var state = new BasicModel.CharacterQuestState { Group = QuestGroup, ActiveQuest = this._heroStatus };
        var killState = new BasicModel.QuestMonsterKillRequirementState { Requirement = killRequirement, KillCount = 1 };
        state.RequirementStates.Add(killState);
        this.SetQuestState(state);

        Assert.That(BotQuestHandler.GetProgress(this._player, this._heroStatus), Is.EqualTo(BotQuestHandler.QuestProgress.InProgress));

        killState.KillCount = 3;
        Assert.That(BotQuestHandler.GetProgress(this._player, this._heroStatus), Is.EqualTo(BotQuestHandler.QuestProgress.ReadyToComplete));

        state.ActiveQuest = null;
        state.LastFinishedQuest = this._heroStatus;
        Assert.That(BotQuestHandler.GetProgress(this._player, this._heroStatus), Is.EqualTo(BotQuestHandler.QuestProgress.Finished));
    }

    /// <summary>
    /// Only one quest of a group can be active. When another one of the chain is running, the bot leaves it
    /// alone instead of trying to start or hand in a quest it does not hold.
    /// </summary>
    [Test]
    public void LeavesAnActiveQuestOfTheSameGroupAlone()
    {
        var otherQuest = this.CreateQuest(9, rewardsCombo: false);
        this._giver.Quests.Add(otherQuest);
        this.SetQuestState(new BasicModel.CharacterQuestState { Group = QuestGroup, ActiveQuest = otherQuest });

        Assert.That(BotQuestHandler.GetProgress(this._player, this._heroStatus), Is.EqualTo(BotQuestHandler.QuestProgress.None));
        Assert.That(BotQuestHandler.GetFarmTarget(this._player, this._heroStatus), Is.Null);
    }

    /// <summary>
    /// The farm target collects the monsters whose kills are still missing and the level bands which can
    /// still drop a required quest item - and nothing which the inventory already covers.
    /// </summary>
    [Test]
    public async ValueTask CollectsTheMonstersAndItemBandsWhichStillAdvanceTheQuestAsync()
    {
        var monster = new BasicModel.MonsterDefinition { Id = Guid.NewGuid(), Number = 2 };
        var killRequirement = new BasicModel.QuestMonsterKillRequirement { Monster = monster, MinimumNumber = 5 };
        var itemDefinition = new Mock<ItemDefinition>();
        itemDefinition.SetupAllProperties();
        itemDefinition.Object.Width = 1;
        itemDefinition.Object.Height = 1;
        var dropGroup = new Mock<DropItemGroup>();
        dropGroup.SetupAllProperties();
        dropGroup.Object.MinimumMonsterLevel = 100;
        dropGroup.Object.MaximumMonsterLevel = 120;
        dropGroup.Object.ItemLevel = 2;
        var itemRequirement = new BasicModel.QuestItemRequirement { Item = itemDefinition.Object, DropItemGroup = dropGroup.Object, MinimumNumber = 1 };
        this._heroStatus.RequiredMonsterKills.Add(killRequirement);
        this._heroStatus.RequiredItems.Add(itemRequirement);
        var state = new BasicModel.CharacterQuestState { Group = QuestGroup, ActiveQuest = this._heroStatus };
        state.RequirementStates.Add(new BasicModel.QuestMonsterKillRequirementState { Requirement = killRequirement, KillCount = 0 });
        this.SetQuestState(state);

        var target = BotQuestHandler.GetFarmTarget(this._player, this._heroStatus);

        Assert.That(target, Is.Not.Null);
        Assert.That(target!.Monsters, Is.EquivalentTo(new[] { monster }));
        Assert.That(target.ItemLevelBands, Is.EquivalentTo(new[] { (100, 120) }));
        Assert.That(target.NeedsKills, Is.True);
        Assert.That(target.NeedsItems, Is.True);
        Assert.That(BotQuestHandler.NeedsQuestFarm(this._player), Is.True);

        var inBand = new BasicModel.MonsterDefinition { Id = Guid.NewGuid(), Number = 3 };
        inBand.Attributes.Add(new BasicModel.MonsterAttribute { AttributeDefinition = Stats.Level, Value = 110 });
        var outOfBand = new BasicModel.MonsterDefinition { Id = Guid.NewGuid(), Number = 4 };
        outOfBand.Attributes.Add(new BasicModel.MonsterAttribute { AttributeDefinition = Stats.Level, Value = 80 });
        Assert.That(target.Matches(inBand), Is.True, "a monster of the item's level band counts");
        Assert.That(target.Matches(outOfBand), Is.False, "a monster outside the band does not");

        // Kills done: only the item is left.
        state.RequirementStates.First().KillCount = 5;
        Assert.That(BotQuestHandler.GetFarmTarget(this._player, this._heroStatus)!.NeedsKills, Is.False);

        // The item in the inventory ends the farm entirely.
        var item = new Mock<Item>();
        item.Setup(i => i.Definition).Returns(itemDefinition.Object);
        item.Object.Level = 2;
        item.Object.ItemSlot = 0;
        Assert.That(await this._player.Inventory!.AddItemAsync(item.Object).ConfigureAwait(false), Is.True);
        Assert.That(BotQuestHandler.GetFarmTarget(this._player, this._heroStatus), Is.Null);
        Assert.That(BotQuestHandler.NeedsQuestFarm(this._player), Is.False);
    }

    /// <summary>
    /// A quest item of a drop group with a fixed item level only counts when the inventory holds that level,
    /// so a bot does not believe it is done with a lower-level copy of the same definition.
    /// </summary>
    [Test]
    public void CountsOnlyQuestItemsOfTheRequiredLevel()
    {
        var itemDefinition = new Mock<ItemDefinition>();
        itemDefinition.SetupAllProperties();
        var dropGroup = new Mock<DropItemGroup>();
        dropGroup.SetupAllProperties();
        dropGroup.Object.ItemLevel = 3;
        var requirement = new BasicModel.QuestItemRequirement { Item = itemDefinition.Object, DropItemGroup = dropGroup.Object, MinimumNumber = 1 };

        Assert.That(BotQuestHandler.CountItem(null, requirement), Is.Zero);

        var wrongLevel = new Mock<Item>();
        wrongLevel.Setup(i => i.Definition).Returns(itemDefinition.Object);
        wrongLevel.Object.Level = 2;
        var rightLevel = new Mock<Item>();
        rightLevel.Setup(i => i.Definition).Returns(itemDefinition.Object);
        rightLevel.Object.Level = 3;

        Assert.That(BotQuestHandler.CountItem(new[] { wrongLevel.Object }, requirement), Is.Zero);
        Assert.That(BotQuestHandler.CountItem(new[] { wrongLevel.Object, rightLevel.Object }, requirement), Is.EqualTo(1));

        dropGroup.Object.ItemLevel = null;
        Assert.That(BotQuestHandler.CountItem(new[] { wrongLevel.Object }, requirement), Is.EqualTo(1), "without a fixed level every copy counts");
    }

    private BasicModel.QuestDefinition CreateQuest(short number, bool rewardsCombo)
    {
        var quest = new BasicModel.QuestDefinition
        {
            Group = QuestGroup,
            Number = number,
            MinimumCharacterLevel = 0,
            Name = new MUnique.OpenMU.Interfaces.LocalizedString("Quest " + number),
        };
        if (rewardsCombo)
        {
            quest.Rewards.Add(new BasicModel.QuestReward { RewardType = QuestRewardType.Attribute, AttributeReward = Stats.IsSkillComboAvailable });
        }

        return quest;
    }

    private BasicModel.MonsterDefinition AddSecondGiver()
    {
        var giver = new BasicModel.MonsterDefinition { Number = 230, NpcWindow = NpcWindow.LegacyQuest };
        giver.Quests.Add(this.CreateQuest(3, rewardsCombo: false));
        giver.Quests.Add(this.CreateQuest(4, rewardsCombo: true));
        this._gameContext.Configuration.Monsters.Add(giver);
        return giver;
    }

    private void SetQuestState(CharacterQuestState state)
        => Mock.Get(this._player.SelectedCharacter!).Setup(c => c.QuestStates).Returns(new List<CharacterQuestState> { state });
}
