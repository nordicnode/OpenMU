// <copyright file="BotLootPolicyTest.cs" company="MUnique">
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
/// Tests the explainable <see cref="BotLootPolicy"/>: which action a bot takes for an item and which
/// reason code it reports for balancing and operator investigations.
/// </summary>
[TestFixture]
public class BotLootPolicyTest
{
    /// <summary>
    /// The shared decision-log setting is clamped to a valid sample percentage.
    /// </summary>
    /// <param name="configured">The configured percentage.</param>
    /// <param name="expected">The effective percentage.</param>
    [TestCase(-10, 0)]
    [TestCase(0, 0)]
    [TestCase(5, 5)]
    [TestCase(100, 100)]
    [TestCase(150, 100)]
    public void LootDecisionLogPercentIsClamped(int configured, int expected)
        => Assert.That(
            new BotConfiguration { LootDecisionLogPercent = configured }.GetEffectiveLootDecisionLogSamplePercent(),
            Is.EqualTo(expected));

    /// <summary>
    /// A bot keeps its most recent decisions for the administration surfaces, newest first, bounded to a
    /// small ring - independently of whether the debug log samples them.
    /// </summary>
    [Test]
    public void BotKeepsRecentLootDecisionsNewestFirst()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());
        var item = CreateItem(1, 8, dropLevel: 10, level: 0, luck: false);
        for (var i = 0; i < 25; i++)
        {
            var decision = i == 24
                ? new BotLootDecision(BotLootAction.Store, BotLootReason.MarketCandidate, "Cheap")
                : new BotLootDecision(BotLootAction.Sell, BotLootReason.VendorTrash);
            bot.RecordLootDecision(item, decision);
        }

        var recent = bot.RecentLootDecisions;

        Assert.That(recent, Has.Count.EqualTo(20));
        Assert.That(recent[0].Code, Is.EqualTo("store/market_candidate (Cheap)"));
        Assert.That(recent[^1].Code, Is.EqualTo("sell/vendor_trash"));
    }

    /// <summary>
    /// The summary shows the time in UTC, also when the timestamp carries another offset - the same
    /// contract the machine-readable timestamp of the report follows.
    /// </summary>
    [Test]
    public void SummaryTimeIsUtc()
    {
        var info = new BotLootDecisionInfo(
            "Dragon Armor +9",
            "sell",
            "vendor_trash",
            null,
            new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(2)));

        Assert.That(info.Summary, Is.EqualTo("10:00:00 sell/vendor_trash: Dragon Armor +9"));
    }

    /// <summary>
    /// A potion the offline AI drinks is kept as consumable stock, not sold as vendor trash.
    /// </summary>
    [Test]
    public async ValueTask PotionIsConsumableStockAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var potion = CreateItem(3, 14, dropLevel: 0, level: 0, luck: false);
        await player.Inventory!.AddItemAsync(InventoryConstants.EquippableSlotsCount, potion).ConfigureAwait(false);

        var decision = Classify(player, null).Single(entry => ReferenceEquals(entry.Item, potion)).Decision;

        Assert.That(decision.Action, Is.EqualTo(BotLootAction.Keep));
        Assert.That(decision.Reason, Is.EqualTo(BotLootReason.ConsumableStock));
    }

    /// <summary>
    /// A usable jewel within the configured stock is kept; only the part above the stock is sold.
    /// </summary>
    [Test]
    public async ValueTask JewelStockIsKeptAndSurplusIsSoldAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var jewels = Enumerable.Range(0, 11)
            .Select(i => CreateItem(13, 14, dropLevel: 0, level: 0, luck: false, personalStoreSellable: true, slot: (byte)(InventoryConstants.EquippableSlotsCount + i)))
            .ToList();
        foreach (var jewel in jewels)
        {
            await player.Inventory!.AddItemAsync(jewel.ItemSlot, jewel).ConfigureAwait(false);
        }

        var decisions = Classify(player, null)
            .Where(entry => jewels.Contains(entry.Item))
            .Select(entry => entry.Decision)
            .ToList();

        Assert.That(decisions.Count(decision => decision.Reason == BotLootReason.JewelStock), Is.EqualTo(10));
        Assert.That(decisions.Count(decision => decision.Reason == BotLootReason.JewelSurplus), Is.EqualTo(1));
        Assert.That(decisions.Single(decision => decision.Reason == BotLootReason.JewelSurplus).Action, Is.EqualTo(BotLootAction.Sell));

        var marketDecision = Classify(player, new BotMerchantConfiguration())
            .Single(entry => entry.Decision.Reason == BotLootReason.MarketCandidate)
            .Decision;
        Assert.That(marketDecision, Is.EqualTo(
            new BotLootDecision(BotLootAction.Store, BotLootReason.MarketCandidate, "Jewel")));
    }

    /// <summary>
    /// A jewel the bot cannot spend can still be a market good when a player can use it - the Jewel
    /// of Creation has its own price in the merchant configuration.
    /// </summary>
    [Test]
    public async ValueTask UnusableJewelCanStillBeMarketGoodAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var creation = CreateItem(22, 14, dropLevel: 0, level: 0, luck: false, personalStoreSellable: true);
        await player.Inventory!.AddItemAsync(creation.ItemSlot, creation).ConfigureAwait(false);

        var decision = Classify(player, new BotMerchantConfiguration())
            .Single(entry => ReferenceEquals(entry.Item, creation))
            .Decision;

        Assert.That(decision, Is.EqualTo(
            new BotLootDecision(BotLootAction.Store, BotLootReason.MarketCandidate, "Jewel")));
    }

    /// <summary>
    /// A good which the market prices is stored; a plain item without value is vendor trash.
    /// </summary>
    [Test]
    public async ValueTask MarketCandidateIsStoredAndPlainItemIsSoldAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var good = CreateItem(1, 8, dropLevel: 60, level: 7, luck: true, personalStoreSellable: true, slot: InventoryConstants.EquippableSlotsCount);
        var trash = CreateItem(2, 8, dropLevel: 10, level: 0, luck: false, personalStoreSellable: true, slot: (byte)(InventoryConstants.EquippableSlotsCount + 1));
        await player.Inventory!.AddItemAsync(good.ItemSlot, good).ConfigureAwait(false);
        await player.Inventory.AddItemAsync(trash.ItemSlot, trash).ConfigureAwait(false);

        var decisions = Classify(player, new BotMerchantConfiguration());

        var marketDecision = decisions.Single(entry => ReferenceEquals(entry.Item, good)).Decision;
        Assert.That(marketDecision, Is.EqualTo(
            new BotLootDecision(BotLootAction.Store, BotLootReason.MarketCandidate, "Cheap")));
        Assert.That(marketDecision.Code, Is.EqualTo("store/market_candidate"));
        Assert.That(decisions.Single(entry => ReferenceEquals(entry.Item, trash)).Decision, Is.EqualTo(
            new BotLootDecision(BotLootAction.Sell, BotLootReason.VendorTrash)));
    }

    /// <summary>
    /// A wanted orb in the backpack is kept instead of being listed on the market or sold to a
    /// merchant - where the skill list may be read, which is the MU Helper tick.
    /// </summary>
    [Test]
    public async ValueTask SkillOrbInBackpackIsKeptAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var characterClass = player.SelectedCharacter!.CharacterClass!;
        var skill = new TestSkill
        {
            Number = 9,
            Name = "Evil Spirit",
            SkillType = SkillType.AreaSkillAutomaticHits,
            AttackDamage = 45,
        };
        skill.QualifiedCharacters.Add(characterClass);
        var definition = CreateDefinition(1, BotProgression.SkillOrbItemGroup, dropLevel: 0);
        definition.Skill = skill;
        definition.QualifiedCharacters.Add(characterClass);
        var orb = CreateItem(definition, slot: InventoryConstants.EquippableSlotsCount);
        await player.Inventory!.AddItemAsync(orb.ItemSlot, orb).ConfigureAwait(false);

        Assert.That(BotSkillHandler.WantsSkillItem(player, orb), Is.True, "the orb must be one the bot wants to learn");

        var decision = Classify(player, new BotMerchantConfiguration(), checkSkillItems: true)
            .Single(entry => ReferenceEquals(entry.Item, orb))
            .Decision;

        Assert.That(decision, Is.EqualTo(new BotLootDecision(BotLootAction.Keep, BotLootReason.SkillItem)));
        Assert.That(decision.IsJunk, Is.False);
    }

    /// <summary>
    /// Gear which beats what the bot wears is an equip decision, not junk.
    /// </summary>
    [Test]
    public async ValueTask UpgradeIsEquippedAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var item = CreateItem(CreateArmorDefinition(player), slot: InventoryConstants.EquippableSlotsCount);
        await player.Inventory!.AddItemAsync(item.ItemSlot, item).ConfigureAwait(false);

        var decision = Classify(player, null).Single(entry => ReferenceEquals(entry.Item, item)).Decision;

        Assert.That(decision.Action, Is.EqualTo(BotLootAction.Equip));
        Assert.That(decision.Reason, Is.EqualTo(BotLootReason.Upgrade));
    }

    /// <summary>
    /// The pickup policy keeps a jewel while the stock has room, and ignores it once the stock is full.
    /// </summary>
    [Test]
    public async ValueTask PickupDecidesByJewelStockAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var jewel = CreateItem(13, 14, dropLevel: 0, level: 0, luck: false);

        Assert.That(BotLootPolicy.DecideForPickup(player, jewel), Is.EqualTo(
            new BotLootDecision(BotLootAction.Keep, BotLootReason.JewelStock)));

        for (var i = 0; i < 10; i++)
        {
            await player.Inventory!.AddItemAsync((byte)(InventoryConstants.EquippableSlotsCount + i), CreateItem(13, 14, 0, 0, false)).ConfigureAwait(false);
        }

        Assert.That(BotLootPolicy.DecideForPickup(player, jewel), Is.EqualTo(
            new BotLootDecision(BotLootAction.Ignore, BotLootReason.JewelSurplus)));
    }

    /// <summary>
    /// A sale is explained as a surplus jewel or as ordinary vendor trash.
    /// </summary>
    [Test]
    public void SaleReasonDistinguishesJewelSurplus()
    {
        Assert.That(BotLootPolicy.DecideForSale(CreateItem(13, 14, 0, 0, false)), Is.EqualTo(
            new BotLootDecision(BotLootAction.Sell, BotLootReason.JewelSurplus)));
        Assert.That(BotLootPolicy.DecideForSale(CreateItem(1, 8, 10, 0, false)), Is.EqualTo(
            new BotLootDecision(BotLootAction.Sell, BotLootReason.VendorTrash)));
    }

    private static IReadOnlyList<(Item Item, BotLootDecision Decision)> Classify(
        Player player,
        BotMerchantConfiguration? marketConfiguration,
        bool checkSkillItems = false)
        => BotLootPolicy.ClassifyBackpack(player, player.Inventory!, marketConfiguration, checkSkillItems);

    private static ItemDefinition CreateArmorDefinition(Player player)
    {
        var definition = CreateDefinition(1, 8, dropLevel: 20);
        var slotType = new Mock<ItemSlotType>();
        slotType.Setup(s => s.ItemSlots).Returns(new List<int> { InventoryConstants.ArmorSlot });
        definition.ItemSlot = slotType.Object;
        definition.QualifiedCharacters.Add(player.SelectedCharacter!.CharacterClass!);
        player.GameContext.Configuration.Items.Add(definition);
        return definition;
    }

    private static Item CreateItem(ItemDefinition definition, byte? slot = null)
        => CreateItem(definition.Number, definition.Group, definition.DropLevel, 0, false, slot: slot, definition: definition);

    private static Item CreateItem(
        short number,
        byte group,
        int dropLevel,
        byte level,
        bool luck,
        bool personalStoreSellable = false,
        byte? slot = null,
        ItemDefinition? definition = null)
    {
        definition ??= CreateDefinition(number, group, dropLevel);
        definition.IsPersonalStoreSellable = personalStoreSellable;

        var itemMock = new Mock<Item>();
        itemMock.SetupAllProperties();
        itemMock.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        itemMock.Setup(i => i.ItemSetGroups).Returns(new List<ItemOfItemSet>());
        var item = itemMock.Object;
        item.Definition = definition;
        item.Level = level;
        item.ItemSlot = slot ?? InventoryConstants.EquippableSlotsCount;
        if (luck)
        {
            item.ItemOptions.Add(new ItemOptionLink { ItemOption = new IncreasableItemOption { OptionType = ItemOptionTypes.Luck } });
        }

        return item;
    }

    private static ItemDefinition CreateDefinition(short number, byte group, int dropLevel)
    {
        var definitionMock = new Mock<ItemDefinition>();
        definitionMock.SetupAllProperties();
        definitionMock.Setup(d => d.QualifiedCharacters).Returns(new List<CharacterClass>());
        definitionMock.Setup(d => d.PossibleItemOptions).Returns(new List<ItemOptionDefinition>());
        definitionMock.Setup(d => d.PossibleItemSetGroups).Returns(new List<ItemSetGroup>());
        definitionMock.Setup(d => d.Requirements).Returns(new List<AttributeRequirement>());

        var definition = definitionMock.Object;
        definition.Number = number;
        definition.Group = group;
        definition.DropLevel = (byte)dropLevel;
        definition.Width = 1;
        definition.Height = 1;
        definition.Durability = 100;
        definition.IsSellableToNpc = true;
        return definition;
    }

    private sealed class TestSkill : Skill
    {
        public TestSkill()
        {
            this.QualifiedCharacters = new List<CharacterClass>();
            this.Requirements = new List<AttributeRequirement>();
        }
    }
}
