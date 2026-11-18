// <copyright file="BotEquipmentPurchasePlanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;

/// <summary>
/// Lets a bot turn the Zen it earns into gear: at a merchant it already visits for potions, repairs and
/// selling its loot, it buys the best equipment upgrade the merchant offers - within a budget - and
/// wears it like a piece it found. This closes the loop which left a bot in the gear it looted on its way
/// up, so a high-level bot is no longer weaker than a player of the same level.
/// </summary>
/// <remarks>
/// Deliberately opt-in (<see cref="BotConfiguration.BuyEquipment"/>): bots don't take part in the player
/// economy, and letting them spend their Zen on shop gear changes what a server's prices are worth. The
/// purchases are budget-capped per trip (<see cref="BotConfiguration.EquipmentBudgetPercent"/>), keep a
/// reserve back, and only buy pieces which beat what the bot wears after checking the same class, stat
/// and level requirements a player faces (see <see cref="BotEquipmentHandler"/>). The bot only *buys*
/// here; the regular equipment pass puts the piece on shortly after (<c>BotNavigator</c>), so the swap
/// itself keeps going through the one validated path.
/// </remarks>
internal static class BotEquipmentPurchasePlanner
{
    /// <summary>
    /// The Zen a bot needs before it considers buying gear at all. Below it, every coin is potion and
    /// warp money; a bot which spent its last Zen on an armor would be stranded.
    /// </summary>
    internal const int MinimumFund = 2_000_000;

    /// <summary>A safety bound per trip - a player also doesn't buy a whole wardrobe in one visit.</summary>
    private const int MaxPurchasesPerTrip = 2;

    private static readonly ItemPriceCalculator PriceCalculator = new();
    private static readonly BuyNpcItemAction BuyAction = new();

    /// <summary>
    /// Determines whether the equipment acquisition phase is switched on for this bot.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <returns><c>true</c> if bots of this server buy equipment.</returns>
    public static bool IsEnabled(Player player)
        => BotFeaturePlugIn.GetConfiguration(player.GameContext)?.BuyEquipment == true;

    /// <summary>
    /// Determines whether the merchant has an upgrade the bot could pay for right now, which makes it
    /// the merchant to walk to (see <see cref="BotShoppingHandler"/>).
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="store">The merchant's stock.</param>
    /// <returns><c>true</c> when the bot would buy something here.</returns>
    public static bool HasAffordableUpgrade(Player player, ItemStorage store)
    {
        var configuration = BotFeaturePlugIn.GetConfiguration(player.GameContext);
        return configuration?.BuyEquipment == true
               && SelectPurchase(player, store, GetBudget(player, configuration)) is not null;
    }

    /// <summary>
    /// Buys equipment upgrades from the merchant the bot is currently trading with. A no-op while the
    /// feature is switched off or the bot has no surplus.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="store">The merchant's stock.</param>
    /// <returns>The number of pieces which were bought.</returns>
    public static async ValueTask<int> BuyUpgradesAsync(OfflinePlayer player, ItemStorage store)
    {
        var configuration = BotFeaturePlugIn.GetConfiguration(player.GameContext);
        if (configuration?.BuyEquipment != true)
        {
            return 0;
        }

        var budget = GetBudget(player, configuration);
        var bought = 0;
        while (bought < MaxPurchasesPerTrip && budget > 0)
        {
            if (SelectPurchase(player, store, budget) is not { } offer)
            {
                break;
            }

            var price = PriceCalculator.CalculateFinalBuyingPrice(offer);
            var moneyBefore = player.Money;
            await BuyAction.BuyItemAsync(player, offer.ItemSlot).ConfigureAwait(false);
            if (player.Money >= moneyBefore)
            {
                break; // refused - no money, no space, or the shop is out
            }

            budget -= moneyBefore - player.Money;
            bought++;
            player.Logger.LogDebug(
                "Bot '{Name}' bought '{Item}' for {Price} zen (budget left: {Budget}).",
                player.Name,
                offer,
                price,
                budget);
        }

        return bought;
    }

    /// <summary>
    /// Gets the Zen a bot may spend on equipment in this visit: the configured share of what it holds
    /// above <see cref="MinimumFund"/>. Everything below the reserve stays with the bot for potions and
    /// warps.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="configuration">The bot configuration.</param>
    /// <returns>The budget in Zen; 0 when the bot has no surplus.</returns>
    internal static long GetBudget(Player player, BotConfiguration configuration)
    {
        var surplus = (long)player.Money - MinimumFund;
        if (surplus <= 0)
        {
            return 0;
        }

        return surplus * configuration.GetEffectiveEquipmentBudgetPercent() / 100;
    }

    /// <summary>
    /// Picks the best piece of the merchant's stock which is an upgrade the bot can pay for: ranked by
    /// how much better it is than the gear it would replace (see
    /// <see cref="BotEquipmentHandler.GetUpgradeMargin"/>), cheapest first among equal upgrades.
    /// Pure decision logic - exposed for unit tests.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="store">The merchant's stock.</param>
    /// <param name="budget">The Zen available for this visit.</param>
    /// <returns>The offer to buy, or <c>null</c> when nothing in stock is worth it.</returns>
    internal static Item? SelectPurchase(Player player, ItemStorage store, long budget)
    {
        if (budget <= 0)
        {
            return null;
        }

        var best = ((Item Item, int Margin, long Price)?)null;
        foreach (var item in store.Items)
        {
            if (item.Definition is not { IsActive: true, IsAmmunition: false })
            {
                continue;
            }

            var margin = BotEquipmentHandler.GetUpgradeMargin(player, item);
            var price = PriceCalculator.CalculateFinalBuyingPrice(item);
            if (margin <= 0 || price <= 0 || price > budget)
            {
                continue;
            }

            if (best is not { } current
                || margin > current.Margin
                || (margin == current.Margin && price < current.Price))
            {
                best = (item, margin, price);
            }
        }

        return best?.Item;
    }
}
