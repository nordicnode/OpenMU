// <copyright file="BotLootPolicy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// The single policy which decides whether a bot keeps, equips, sells or stores an item. The
/// shopping and pickup handlers consume the result instead of each carrying their own copy of the
/// rules, and the equipment handler reports the same reason when it applies a swap.
/// </summary>
/// <remarks>
/// The policy composes the existing specialized rules: <see cref="BotEquipmentHandler"/> evaluates
/// gear, <see cref="BotJewelHandler"/> owns the jewel stock, and <see cref="BotMerchandise"/> prices
/// market goods. Skill orbs are checked only when the caller asks for it: reading the skill list is
/// only safe on the MU Helper tick, not on the navigator's timer, so callers there leave it off.
/// </remarks>
internal static class BotLootPolicy
{
    /// <summary>
    /// Classifies the backpack of a bot. Items which are sold and items which are stored are both
    /// reported as junk (<see cref="BotLootDecision.IsJunk"/>); the action tells them apart.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <param name="inventory">Its inventory.</param>
    /// <param name="marketConfiguration">The merchant configuration, or <c>null</c> when the market is switched off.</param>
    /// <param name="checkSkillItems">Whether to keep looted orbs and scrolls the bot still wants to learn. Only
    /// pass <c>true</c> from the MU Helper tick, where reading the skill list cannot race the learn pass that
    /// mutates it.</param>
    /// <returns>One decision per item of the inventory, in inventory order.</returns>
    internal static IReadOnlyList<(Item Item, BotLootDecision Decision)> ClassifyBackpack(
        Player player,
        IStorage inventory,
        BotMerchantConfiguration? marketConfiguration,
        bool checkSkillItems = false)
    {
        var keptJewels = new Dictionary<ItemIdentifier, int>();
        var decisions = new List<(Item Item, BotLootDecision Decision)>();
        foreach (var item in inventory.Items)
        {
            decisions.Add((item, ClassifyBackpackItem(player, item, keptJewels, marketConfiguration, checkSkillItems)));
        }

        return decisions;
    }

    /// <summary>
    /// Gets the backpack items the bot would sell or store, without building the full audit list.
    /// This is the cheaper query for the trip planner, which only needs to know whether anything is
    /// waiting for a merchant.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <param name="inventory">Its inventory.</param>
    /// <param name="marketConfiguration">The merchant configuration, or <c>null</c> when the market is switched off.</param>
    /// <param name="checkSkillItems">Whether to keep looted orbs and scrolls the bot still wants to learn. Only
    /// pass <c>true</c> from the MU Helper tick, where reading the skill list cannot race the learn pass that
    /// mutates it.</param>
    /// <returns>The junk items, in inventory order.</returns>
    internal static List<Item> GetJunk(
        Player player,
        IStorage inventory,
        BotMerchantConfiguration? marketConfiguration,
        bool checkSkillItems = false)
    {
        var keptJewels = new Dictionary<ItemIdentifier, int>();
        var junk = new List<Item>();
        foreach (var item in inventory.Items)
        {
            if (ClassifyBackpackItem(player, item, keptJewels, marketConfiguration, checkSkillItems).IsJunk)
            {
                junk.Add(item);
            }
        }

        return junk;
    }

    /// <summary>
    /// Decides whether a dropped item is worth picking up. Only the bot-specific part is evaluated
    /// here; the helper configuration still gates which categories the bot collects.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <param name="item">The dropped item.</param>
    /// <returns>The decision.</returns>
    internal static BotLootDecision DecideForPickup(Player player, Item item)
    {
        if (item.Definition is not { } definition)
        {
            return new(BotLootAction.Ignore, BotLootReason.UnknownItem);
        }

        if (WantsQuestItem(player, item))
        {
            return new(BotLootAction.Keep, BotLootReason.QuestItem);
        }

        var identifier = new ItemIdentifier(definition.Number, definition.Group);
        if (IsPotion(identifier))
        {
            return new(BotLootAction.Keep, BotLootReason.ConsumableStock);
        }

        if (BotJewelHandler.UsableJewels.Contains(identifier))
        {
            return BotJewelHandler.WantsMoreOf(player, item)
                ? new(BotLootAction.Keep, BotLootReason.JewelStock)
                : new(BotLootAction.Ignore, BotLootReason.JewelSurplus);
        }

        if (BotJewelHandler.UnusableJewels.Contains(identifier))
        {
            return new(BotLootAction.Ignore, BotLootReason.UnusableJewel);
        }

        if (BotEquipmentHandler.IsUpgradeFor(player, item))
        {
            return new(BotLootAction.Equip, BotLootReason.Upgrade);
        }

        if (BotSkillHandler.WantsSkillItem(player, item))
        {
            return new(BotLootAction.Keep, BotLootReason.SkillItem);
        }

        return new(BotLootAction.Ignore, BotLootReason.NotAnUpgrade);
    }

    /// <summary>
    /// Checks whether an active quest still needs this particular item and level.
    /// </summary>
    /// <param name="player">The player undertaking the quest.</param>
    /// <param name="item">The dropped item.</param>
    /// <returns><c>true</c> when the inventory holds fewer than the required number.</returns>
    internal static bool WantsQuestItem(Player player, Item item)
    {
        if (item.Definition is null || player.Inventory is not { } inventory || player.SelectedCharacter?.QuestStates is not { } states)
        {
            return false;
        }

        return states.Where(state => state.ActiveQuest is not null)
            .SelectMany(state => state.ActiveQuest!.RequiredItems)
            .Where(requirement => Equals(requirement.Item, item.Definition)
                                  && (requirement.DropItemGroup?.ItemLevel is not { } level || level == item.Level))
            .Any(requirement => inventory.Items.Count(existing => Equals(existing.Definition, requirement.Item)
                && (requirement.DropItemGroup?.ItemLevel is not { } requiredLevel || existing.Level == requiredLevel)) < requirement.MinimumNumber);
    }

    /// <summary>
    /// Explains why an item is sold to a merchant NPC. Used for the audit log of an actual sale,
    /// after the market had its chance to take the item.
    /// </summary>
    /// <param name="item">The sold item.</param>
    /// <returns>The sale decision.</returns>
    internal static BotLootDecision DecideForSale(Item item)
    {
        if (item.Definition is not { } definition)
        {
            return new(BotLootAction.Sell, BotLootReason.VendorTrash);
        }

        var identifier = new ItemIdentifier(definition.Number, definition.Group);
        if (BotJewelHandler.UsableJewels.Contains(identifier))
        {
            return new(BotLootAction.Sell, BotLootReason.JewelSurplus);
        }

        if (BotJewelHandler.UnusableJewels.Contains(identifier))
        {
            return new(BotLootAction.Sell, BotLootReason.UnusableJewel);
        }

        return new(BotLootAction.Sell, BotLootReason.VendorTrash);
    }

    private static BotLootDecision ClassifyBackpackItem(
        Player player,
        Item item,
        IDictionary<ItemIdentifier, int> keptJewels,
        BotMerchantConfiguration? marketConfiguration,
        bool checkSkillItems)
    {
        if (item.ItemSlot < InventoryConstants.EquippableSlotsCount)
        {
            return new(BotLootAction.Ignore, BotLootReason.Equipped);
        }

        if (item.Definition is not { } definition)
        {
            return new(BotLootAction.Ignore, BotLootReason.UnknownItem);
        }

        // Protect active requirements even when a server uses an ordinary item for its quest.
        // Already collected requirements no longer satisfy WantsQuestItem, but are still needed
        // for the hand-in and must not be sold, stored or discarded. Read active quest state only
        // on the serialized helper tick, like the skill list; the navigator's estimate may be conservative.
        if (definition.IsQuestItem
            || (checkSkillItems && player.SelectedCharacter?.QuestStates?.Any(state => state.ActiveQuest?.RequiredItems.Any(requirement =>
                Equals(requirement.Item, definition)
                && (requirement.DropItemGroup?.ItemLevel is not { } level || level == item.Level)) == true) == true))
        {
            return new(BotLootAction.Keep, BotLootReason.QuestItem);
        }

        // An orb or scroll waiting for the learn pass must never be sold or listed on the market. Reading
        // the skill list is only safe on the MU Helper tick, which is why the caller has to ask for it.
        if (checkSkillItems && BotSkillHandler.WantsSkillItem(player, item))
        {
            return new(BotLootAction.Keep, BotLootReason.SkillItem);
        }

        if (definition.IsAmmunition)
        {
            return new(BotLootAction.Ignore, BotLootReason.Ammunition);
        }

        if (!definition.IsSellableToNpc)
        {
            return new(BotLootAction.Ignore, BotLootReason.Unsellable);
        }

        if (item.IsWing())
        {
            return new(BotLootAction.Ignore, BotLootReason.Wing);
        }

        var identifier = new ItemIdentifier(definition.Number, definition.Group);
        if (IsPotion(identifier))
        {
            return new(BotLootAction.Keep, BotLootReason.ConsumableStock);
        }

        if (BotJewelHandler.UsableJewels.Contains(identifier))
        {
            var kept = keptJewels.TryGetValue(identifier, out var count) ? count : 0;
            if (kept < BotJewelHandler.GetStockLimit(player, identifier))
            {
                keptJewels[identifier] = kept + 1;
                return new(BotLootAction.Keep, BotLootReason.JewelStock);
            }

            // A surplus jewel is one of the market's goods, not just NPC trash, when the merchant
            // plugin is on. Only a server without the market sells it directly.
            return TryMarketDecision(item, marketConfiguration) ?? new(BotLootAction.Sell, BotLootReason.JewelSurplus);
        }

        if (BotJewelHandler.UnusableJewels.Contains(identifier))
        {
            // Most of these have no price at all, but a Jewel of Creation can still be a market good:
            // the bot cannot spend it, while a player can. Let the market price decide before it becomes NPC trash.
            return TryMarketDecision(item, marketConfiguration) ?? new(BotLootAction.Sell, BotLootReason.UnusableJewel);
        }

        if (BotEquipmentHandler.IsUpgradeFor(player, item))
        {
            return new(BotLootAction.Equip, BotLootReason.Upgrade);
        }

        return TryMarketDecision(item, marketConfiguration) ?? new(BotLootAction.Sell, BotLootReason.VendorTrash);
    }

    private static BotLootDecision? TryMarketDecision(Item item, BotMerchantConfiguration? marketConfiguration)
    {
        if (marketConfiguration is null)
        {
            return null;
        }

        var priceClass = BotMerchandise.Classify(item, marketConfiguration);
        return priceClass == BotMerchandise.PriceClass.None
            ? null
            : new(BotLootAction.Store, BotLootReason.MarketCandidate, priceClass.ToString());
    }

    private static bool IsPotion(ItemIdentifier identifier)
        => HealingHandler.HealthPotionPriority.Contains(identifier)
           || HealingHandler.ManaPotionPriority.Contains(identifier)
           || HealingHandler.ShieldPotionPriority.Contains(identifier);
}
