// <copyright file="HealingHandlerTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Offline;

using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.ItemConsumeActions;
using MUnique.OpenMU.GameServer.RemoteView.MuHelper;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests for <see cref="HealingHandler"/>.
/// </summary>
[TestFixture]
public class HealingHandlerTests
{
    private IGameContext _gameContext = null!;

    /// <summary>
    /// Sets up a fresh game context before each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
    }

    /// <summary>
    /// Tests that <see cref="HealingHandler.PerformHealthRecoveryAsync"/> does nothing
    /// when config is null.
    /// </summary>
    [Test]
    public async ValueTask DoesNothingWhenConfigNullAsync()
    {
        // Arrange
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        var handler = new HealingHandler(player, null);

        // Act
        await handler.PerformHealthRecoveryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Tests that <see cref="HealingHandler.PerformHealthRecoveryAsync"/> does not consume
    /// a potion when the player's HP is above the threshold.
    /// </summary>
    [Test]
    public async ValueTask DoesNotUsePotionAboveThresholdAsync()
    {
        // Arrange
        var player = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        var maxHp = player.Attributes![Stats.MaximumHealth];
        player.Attributes[Stats.CurrentHealth] = maxHp * 0.9f;

        var potion = this.CreateHealthPotion();
        await player.Inventory!.AddItemAsync((byte)(InventoryConstants.FirstEquippableItemSlotIndex + 12), potion).ConfigureAwait(false);

        var config = new MuHelperSettings
        {
            UseHealPotion = true,
            PotionThresholdPercent = 50,
        };

        var handler = new HealingHandler(player, config);

        // Act
        await handler.PerformHealthRecoveryAsync().ConfigureAwait(false);

        // Assert
        Assert.That(player.Inventory?.GetItem(potion.ItemSlot), Is.Not.Null);
    }

    /// <summary>
    /// A nearby party member is healed only when both players are on the same map.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async ValueTask PartyHealingRequiresSameMapAsync(bool sameMap)
    {
        var healer = await this.CreateOfflinePlayerAsync().ConfigureAwait(false);
        var member = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var map = new GameMap(new Persistence.BasicModel.GameMapDefinition { Number = 1 }, TimeSpan.FromMinutes(1), 8);
        healer.SetCurrentMapSilently(map);
        member.SetCurrentMapSilently(sameMap ? map : new GameMap(new Persistence.BasicModel.GameMapDefinition { Number = 2 }, TimeSpan.FromMinutes(1), 8));
        healer.Position = member.Position = new Point(100, 100);
        healer.IsAlive = member.IsAlive = true;
        await healer.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        await member.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        var party = this._gameContext.PartyManager.CreateParty();
        await party.AddAsync(healer).ConfigureAwait(false);
        await party.AddAsync(member).ConfigureAwait(false);
        var effect = new Persistence.BasicModel.MagicEffectDefinition();
        effect.PowerUpDefinitions.Add(new Persistence.BasicModel.PowerUpDefinition
        {
            TargetAttribute = Stats.CurrentHealth,
            Boost = new Persistence.BasicModel.PowerUpDefinitionValue { ConstantValue = { Value = 10 } },
        });
        var heal = new Persistence.BasicModel.Skill { Number = 26, SkillType = SkillType.Regeneration, MagicEffectDef = effect };
        await healer.SkillList!.AddLearnedSkillAsync(heal).ConfigureAwait(false);
        member.Attributes![Stats.CurrentHealth] = 10;
        var handler = new HealingHandler(healer, new MuHelperSettings { AutoHealParty = true, HealPartyThresholdPercent = 60, HuntingRange = 6 });

        await handler.PerformHealthRecoveryAsync().ConfigureAwait(false);

        Assert.That(member.Attributes[Stats.CurrentHealth], Is.EqualTo(sameMap ? 20 : 10));
    }

    /// <summary>
    /// Shield potions are reserved for bots with a low shield and a player opponent, and obey the potion cooldown.
    /// </summary>
    [TestCase(true, true, 30, false, true)]
    [TestCase(true, true, 50, false, false)]
    [TestCase(true, false, 30, false, false)]
    [TestCase(false, true, 30, false, false)]
    [TestCase(true, true, 30, true, false)]
    public async ValueTask BotShieldPotionRequiresPlayerFightAsync(bool isBot, bool opponent, int shieldPercent, bool cooldown, bool consumed)
    {
        var gameContext = GameContextTestHelper.CreateGameContext([
            new PlugInConfiguration { TypeId = typeof(LargeShieldPotionConsumeHandlerPlugIn).GUID, IsActive = true },
        ]);
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        bot.Account!.IsBot = isBot;
        bot.IsAlive = true;
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        bot.Attributes![Stats.CurrentShield] = bot.Attributes[Stats.MaximumShield] * shieldPercent / 100;
        if (cooldown)
        {
            bot.PotionCooldownUntil = DateTime.UtcNow.AddMinutes(1);
        }

        var potion = new Persistence.BasicModel.Item
        {
            Definition = new Persistence.BasicModel.ItemDefinition { Group = 14, Number = 37, Width = 1, Height = 1 },
            Durability = 2,
        };
        await bot.Inventory!.AddItemAsync((byte)(InventoryConstants.FirstEquippableItemSlotIndex + 12), potion).ConfigureAwait(false);
        var handler = new HealingHandler(bot, new MuHelperSettings());

        await handler.PerformBotShieldRecoveryAsync(opponent).ConfigureAwait(false);

        Assert.That(potion.Durability, Is.EqualTo(consumed ? 1 : 2));
    }

    private async ValueTask<OfflinePlayer> CreateOfflinePlayerAsync()
    {
        return await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
    }

    private MUnique.OpenMU.DataModel.Entities.Item CreateHealthPotion()
    {
        var definition = new Mock<MUnique.OpenMU.DataModel.Configuration.Items.ItemDefinition>();
        definition.SetupAllProperties();
        definition.Setup(d => d.BasePowerUpAttributes).Returns(new List<ItemBasePowerUpDefinition>());
        definition.Setup(d => d.PossibleItemOptions).Returns(new List<ItemOptionDefinition>());
        definition.Object.Number = ItemConstants.SmallHealingPotion.Number!.Value;
        definition.Object.Group = ItemConstants.SmallHealingPotion.Group;

        var item = new Mock<MUnique.OpenMU.DataModel.Entities.Item>();
        item.SetupAllProperties();
        item.Setup(i => i.Definition).Returns(definition.Object);
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Setup(i => i.ItemSetGroups).Returns(new List<ItemOfItemSet>());
        item.Object.Durability = 1;

        return item.Object;
    }

}
