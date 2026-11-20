// <copyright file="BotStoreBrowserTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests the offer choice of <see cref="BotStoreBrowser"/>: only Zen prices the bot can pay, and the
/// cheapest useful offer first. The store browsing itself runs in the bot tick; the decision is pure.
/// </summary>
[TestFixture]
public class BotStoreBrowserTest
{
    private IGameContext _gameContext = null!;
    private Player _bot = null!;

    /// <summary>Creates a fresh game context and bot for every test.</summary>
    [SetUp]
    public async ValueTask SetUpAsync()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
        this._bot = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
    }

    /// <summary>
    /// Of two jewels the bot can afford, the cheaper one wins; an offer priced above the budget is
    /// ignored, and with no affordable offer at all there is nothing to buy.
    /// </summary>
    [Test]
    public async ValueTask PicksTheCheapestAffordableJewelAsync()
    {
        var expensiveSeller = await this.CreateSellerAsync(price: 5_000).ConfigureAwait(false);
        var cheapSeller = await this.CreateSellerAsync(price: 1_000).ConfigureAwait(false);

        var offer = BotStoreBrowser.SelectOffer(this._bot, [expensiveSeller, cheapSeller], budget: 2_000);

        Assert.That(offer, Is.Not.Null);
        Assert.That(offer!.Value.Seller, Is.SameAs(cheapSeller));
        Assert.That(offer.Value.Price, Is.EqualTo(1_000));

        Assert.That(BotStoreBrowser.SelectOffer(this._bot, [expensiveSeller, cheapSeller], budget: 500), Is.Null);
        Assert.That(BotStoreBrowser.SelectOffer(this._bot, [], budget: 2_000), Is.Null);
    }

    /// <summary>
    /// A price paid in jewels or account coins is not a Zen price a bot may take, and an item without a
    /// price is not for sale at all.
    /// </summary>
    [Test]
    public async ValueTask IgnoresNonZenPricesAndUnpricedGoodsAsync()
    {
        var seller = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var store = seller.ShopStorage!;
        var jewelPriced = CreateJewel(price: 1_000);
        jewelPriced.StorePriceItemDefinition = CreateDefinition(0, 0);
        var unpriced = CreateJewel(price: 0);

        Assert.That(await store.AddItemAsync(InventoryConstants.FirstStoreItemSlotIndex, jewelPriced).ConfigureAwait(false), Is.True);
        Assert.That(await store.AddItemAsync((byte)(InventoryConstants.FirstStoreItemSlotIndex + 2), unpriced).ConfigureAwait(false), Is.True);

        Assert.That(BotStoreBrowser.SelectOffer(this._bot, [seller], budget: 10_000), Is.Null);
    }

    /// <summary>
    /// The offer names the seller, the slot and the item, so the tick can buy exactly what was chosen.
    /// </summary>
    [Test]
    public async ValueTask NamesTheChosenSlotAndItemAsync()
    {
        var seller = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var jewel = CreateJewel(price: 700);
        var slot = (byte)(InventoryConstants.FirstStoreItemSlotIndex + 4);
        Assert.That(await seller.ShopStorage!.AddItemAsync(slot, jewel).ConfigureAwait(false), Is.True);

        var offer = BotStoreBrowser.SelectOffer(this._bot, [seller], budget: 700);

        Assert.That(offer, Is.Not.Null);
        Assert.That(offer!.Value.Seller, Is.SameAs(seller));
        Assert.That(offer.Value.Slot, Is.EqualTo(slot));
        Assert.That(offer.Value.Item, Is.SameAs(jewel));
        Assert.That(offer.Value.Price, Is.EqualTo(700));
    }

    private async ValueTask<Player> CreateSellerAsync(int price)
    {
        var seller = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var store = seller.ShopStorage!;
        Assert.That(await store.AddItemAsync(InventoryConstants.FirstStoreItemSlotIndex, CreateJewel(price)).ConfigureAwait(false), Is.True);
        store.StoreOpen = true;
        return seller;
    }

    private static Item CreateJewel(int price)
    {
        var item = new Mock<Item>();
        item.SetupAllProperties();
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Setup(i => i.ItemSetGroups).Returns(new List<ItemOfItemSet>());
        item.Object.Definition = CreateDefinition(ItemConstants.JewelOfBless.Number!.Value, ItemConstants.JewelOfBless.Group);
        item.Object.StorePrice = price;
        return item.Object;
    }

    private static ItemDefinition CreateDefinition(short number, byte group)
    {
        var definition = new Mock<ItemDefinition>();
        definition.SetupAllProperties();
        definition.Setup(d => d.PossibleItemOptions).Returns(new List<ItemOptionDefinition>());
        definition.Setup(d => d.PossibleItemSetGroups).Returns(new List<ItemSetGroup>());
        definition.Object.Number = number;
        definition.Object.Group = group;
        definition.Object.Width = 1;
        definition.Object.Height = 1;
        return definition.Object;
    }
}
