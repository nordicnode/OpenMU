// <copyright file="BotStoreBrowser.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.PlayerStore;
using MUnique.OpenMU.GameLogic.PlayerShopCurrencies;

/// <summary>
/// Makes the player market two-way: while a bot waits at its own stall it also looks at the stores of
/// the humans around it and buys a fair offer - a jewel, or a piece which beats the gear it wears. It
/// only buys for Zen, within the configured equipment budget and only from stores it can reach.
/// </summary>
/// <remarks>
/// Deliberately tied to <see cref="BotConfiguration.BuyEquipment"/>: the same opt-in which lets bots
/// buy from NPC merchants. A server which keeps its player economy free of bots simply leaves it off.
/// The purchase itself runs in the bot's MU Helper tick (see <see cref="OfflinePlayer.PendingBotActions"/>),
/// like every other inventory mutation of a bot.
/// </remarks>
internal static class BotStoreBrowser
{
    /// <summary>The distance (tiles) up to which a bot looks at the stores around it.</summary>
    internal const int BrowseRange = 10;

    /// <summary>How often a bot waiting at its stall looks around for offers.</summary>
    internal static readonly TimeSpan BrowseInterval = TimeSpan.FromSeconds(45);

    /// <summary>The jewels a bot is always happy to find in a player store.</summary>
    private static readonly ItemIdentifier[] WantedJewels =
    [
        ItemConstants.JewelOfBless,
        ItemConstants.JewelOfSoul,
        ItemConstants.JewelOfLife,
        ItemConstants.JewelOfCreation,
        ItemConstants.JewelOfChaos,
    ];

    private static readonly BuyRequestAction BuyAction = new();

    /// <summary>
    /// Determines whether bots of this server trade with players at all.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns><c>true</c> if the equipment budget allows purchases.</returns>
    internal static bool IsEnabled(Player bot)
        => BotFeaturePlugIn.GetConfiguration(bot.GameContext) is { BuyEquipment: true, EquipmentBudgetPercent: > 0 };

    /// <summary>
    /// Buys the best affordable offer of the open player stores around the bot, if there is one.
    /// </summary>
    /// <param name="bot">The bot, standing at its stall.</param>
    /// <returns><c>true</c> if an item was bought.</returns>
    internal static async ValueTask<bool> TryBuyAsync(OfflinePlayer bot)
    {
        if (!IsEnabled(bot) || bot.CurrentMap is not { } map)
        {
            return false;
        }

        var configuration = BotFeaturePlugIn.GetConfiguration(bot.GameContext)!;
        var budget = BotEquipmentPurchasePlanner.GetBudget(bot, configuration);
        if (budget <= 0)
        {
            return false;
        }

        var sellers = map.GetAttackablesInRange(bot.Position, BrowseRange)
            .OfType<Player>()
            .Where(player => !ReferenceEquals(player, bot) && player is not OfflinePlayer && (player.ShopStorage?.StoreOpen ?? false))
            .ToList();
        if (SelectOffer(bot, sellers, budget) is not { } offer)
        {
            return false;
        }

        await BuyAction.BuyItemAsync(bot, offer.Seller, offer.Slot).ConfigureAwait(false);
        bot.Logger.LogDebug(
            "Bot '{Name}' bought '{Item}' from the store of '{Seller}' for {Price} zen.",
            bot.Name,
            offer.Item,
            offer.Seller.Name,
            offer.Price);
        return true;
    }

    /// <summary>
    /// Picks the best offer of the given stores which the bot can pay for: a jewel first, then the
    /// equipment upgrade with the largest margin over what the bot wears. Only Zen prices are
    /// considered - paying in jewels would need a stock check the shop action does itself, but a bot
    /// should not hand out the jewels it collects for its own gear.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="sellers">The players with an open store.</param>
    /// <param name="budget">The Zen available.</param>
    /// <returns>The offer to buy, or <c>null</c>.</returns>
    internal static (Player Seller, byte Slot, Item Item, int Price)? SelectOffer(Player bot, IReadOnlyCollection<Player> sellers, long budget)
    {
        (Player Seller, byte Slot, Item Item, int Price, int Score)? best = null;
        foreach (var seller in sellers)
        {
            if (seller.ShopStorage is not { } store)
            {
                continue;
            }

            foreach (var item in store.Items)
            {
                if (item.GetStorePrice() is not { Kind: ShopPriceKind.Zen, Amount: > 0 } price
                    || price.Amount > budget
                    || item.Definition is not { } definition)
                {
                    continue;
                }

                var score = GetScore(bot, item);
                if (score <= 0)
                {
                    continue;
                }

                if (best is not { } current
                    || score > current.Score
                    || (score == current.Score && price.Amount < current.Price))
                {
                    best = (seller, item.ItemSlot, item, price.Amount, score);
                }
            }
        }

        return best is { } value ? (value.Seller, value.Slot, value.Item, value.Price) : null;
    }

    /// <summary>
    /// Rates an offer for the bot: a jewel is always useful, a skill item it still needs is next, and
    /// a piece of gear only counts when it beats what the bot wears.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="item">The offered item.</param>
    /// <returns>The score; 0 when the bot has no use for it.</returns>
    private static int GetScore(Player bot, Item item)
    {
        if (item.Definition is not { } definition)
        {
            return 0;
        }

        if (WantedJewels.Contains(new ItemIdentifier(definition.Number, definition.Group)))
        {
            return 10_000;
        }

        if (BotSkillHandler.WantsSkillItem(bot, item))
        {
            return 5_000;
        }

        var margin = BotEquipmentHandler.GetUpgradeMargin(bot, item);
        return margin > 0 ? margin : 0;
    }
}
