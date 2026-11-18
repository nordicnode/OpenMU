// <copyright file="BotEquipmentPurchasePlannerTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests the equipment acquisition policy of <see cref="BotEquipmentPurchasePlanner"/>: which shop offer
/// a bot buys (an upgrade it can wear and pay for), how the budget caps it, and that the whole phase
/// stays off unless the server opts in.
/// </summary>
[TestFixture]
public class BotEquipmentPurchasePlannerTest
{
    private const byte StaffGroup = 5;
    private const byte ArmorGroup = 8;

    /// <summary>
    /// The best affordable upgrade is bought - not the first offer, and not a piece which is worse than
    /// what the bot already wears.
    /// </summary>
    [Test]
    public async ValueTask SelectsTheBestAffordableUpgradeAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, CreateDefinition(player, StaffGroup, 1, dropLevel: 10), InventoryConstants.LeftHandSlot).ConfigureAwait(false);

        var worse = CreateItem(CreateDefinition(player, StaffGroup, 2, dropLevel: 5));
        var better = CreateItem(CreateDefinition(player, StaffGroup, 3, dropLevel: 60));
        var store = CreateStore(worse, better);

        var purchase = BotEquipmentPurchasePlanner.SelectPurchase(player, store, int.MaxValue);

        Assert.That(purchase, Is.SameAs(better));
    }

    /// <summary>
    /// An offer above the budget stays on the shelf, even though it would be the better upgrade.
    /// </summary>
    [Test]
    public async ValueTask RespectsTheBudgetAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, CreateDefinition(player, StaffGroup, 1, dropLevel: 5), InventoryConstants.LeftHandSlot).ConfigureAwait(false);

        var affordable = CreateItem(CreateDefinition(player, StaffGroup, 2, dropLevel: 20));
        var expensive = CreateItem(CreateDefinition(player, StaffGroup, 3, dropLevel: 120));
        var store = CreateStore(affordable, expensive);

        var affordablePrice = new ItemPriceCalculator().CalculateFinalBuyingPrice(affordable);
        var purchase = BotEquipmentPurchasePlanner.SelectPurchase(player, store, affordablePrice);

        Assert.That(purchase, Is.SameAs(affordable));
    }

    /// <summary>
    /// With nothing affordable, or no upgrade at all, the planner buys nothing.
    /// </summary>
    [Test]
    public async ValueTask BuysNothingWithoutAnAffordableUpgradeAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, CreateDefinition(player, StaffGroup, 1, dropLevel: 80), InventoryConstants.LeftHandSlot).ConfigureAwait(false);
        var store = CreateStore(CreateItem(CreateDefinition(player, StaffGroup, 2, dropLevel: 5)));

        Assert.That(BotEquipmentPurchasePlanner.SelectPurchase(player, store, 0), Is.Null);
        Assert.That(BotEquipmentPurchasePlanner.SelectPurchase(player, store, int.MaxValue), Is.Null);
    }

    /// <summary>
    /// A piece of a slot the bot already fills better is no upgrade, however cheap it is.
    /// </summary>
    [Test]
    public async ValueTask IgnoresGearWhichIsNoUpgradeAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, CreateDefinition(player, ArmorGroup, 1, dropLevel: 90, slot: InventoryConstants.ArmorSlot), InventoryConstants.ArmorSlot).ConfigureAwait(false);
        var store = CreateStore(CreateItem(CreateDefinition(player, ArmorGroup, 2, dropLevel: 10, slot: InventoryConstants.ArmorSlot)));

        Assert.That(BotEquipmentPurchasePlanner.SelectPurchase(player, store, int.MaxValue), Is.Null);
    }

    /// <summary>
    /// The budget is the configured share of what the bot holds above the minimum fund: a bot which
    /// only carries its travel money has no equipment budget at all.
    /// </summary>
    [Test]
    public async ValueTask BudgetIsAShareOfTheSurplusAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Money = BotEquipmentPurchasePlanner.MinimumFund;
        var configuration = new BotConfiguration { BuyEquipment = true, EquipmentBudgetPercent = 50 };

        Assert.That(BotEquipmentPurchasePlanner.GetBudget(player, configuration), Is.Zero);

        player.Money = BotEquipmentPurchasePlanner.MinimumFund + 1_000_000;
        Assert.That(BotEquipmentPurchasePlanner.GetBudget(player, configuration), Is.EqualTo(500_000));
    }

    /// <summary>
    /// The acquisition phase is opt-in: with the feature switched off, a merchant's upgrade is never
    /// bought.
    /// </summary>
    [Test]
    public async ValueTask DoesNotBuyWhileTheFeatureIsOffAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, CreateDefinition(player, StaffGroup, 1, dropLevel: 5), InventoryConstants.LeftHandSlot).ConfigureAwait(false);
        player.Money = BotEquipmentPurchasePlanner.MinimumFund * 4;
        var store = CreateStore(CreateItem(CreateDefinition(player, StaffGroup, 2, dropLevel: 60)));

        var plugin = new BotFeaturePlugIn { Configuration = new BotConfiguration() };
        player.GameContext.FeaturePlugIns.AddPlugIn(plugin, true);
        Assert.That(BotEquipmentPurchasePlanner.HasAffordableUpgrade(player, store), Is.False);

        plugin.Configuration.BuyEquipment = true;
        Assert.That(BotEquipmentPurchasePlanner.HasAffordableUpgrade(player, store), Is.True);
    }

    private static ItemStorage CreateStore(params Item[] items)
    {
        var store = new Mock<ItemStorage>();
        store.SetupAllProperties();
        store.Setup(s => s.Items).Returns(items.ToList());
        return store.Object;
    }

    private static ItemDefinition CreateDefinition(Player player, byte group, short number, byte dropLevel, byte width = 1, int? slot = null)
    {
        var definitionMock = new Mock<ItemDefinition>();
        definitionMock.SetupAllProperties();
        definitionMock.Setup(d => d.QualifiedCharacters).Returns(new List<CharacterClass>());
        definitionMock.Setup(d => d.PossibleItemOptions).Returns(new List<ItemOptionDefinition>());
        definitionMock.Setup(d => d.PossibleItemSetGroups).Returns(new List<ItemSetGroup>());
        definitionMock.Setup(d => d.BasePowerUpAttributes).Returns(new List<ItemBasePowerUpDefinition>());
        definitionMock.Setup(d => d.Requirements).Returns(new List<AttributeRequirement>());

        var slotTypeMock = new Mock<ItemSlotType>();
        var targetSlot = slot ?? InventoryConstants.LeftHandSlot;
        var slots = targetSlot == InventoryConstants.LeftHandSlot && group <= 6 && width < 2
            ? new List<int> { InventoryConstants.LeftHandSlot, InventoryConstants.RightHandSlot }
            : new List<int> { targetSlot };
        slotTypeMock.Setup(s => s.ItemSlots).Returns(slots);
        definitionMock.Setup(d => d.ItemSlot).Returns(slotTypeMock.Object);

        var definition = definitionMock.Object;
        definition.Group = group;
        definition.Number = number;
        definition.Width = width;
        definition.Height = 2;
        definition.Durability = 100;
        definition.DropLevel = dropLevel;
        definition.IsActive = true;
        definition.QualifiedCharacters.Add(player.SelectedCharacter!.CharacterClass!);
        player.GameContext.Configuration.Items.Add(definition);
        return definition;
    }

    private static Item CreateItem(ItemDefinition definition)
    {
        var itemMock = new Mock<Item>();
        itemMock.SetupAllProperties();
        itemMock.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        itemMock.Setup(i => i.ItemSetGroups).Returns(new List<ItemOfItemSet>());
        var item = itemMock.Object;
        item.Definition = definition;
        item.Durability = definition.Durability;
        return item;
    }

    private static async ValueTask<Item> WearAsync(Player player, ItemDefinition definition, byte slot)
    {
        var item = CreateItem(definition);
        await player.Inventory!.AddItemAsync(slot, item).ConfigureAwait(false);
        return item;
    }
}
