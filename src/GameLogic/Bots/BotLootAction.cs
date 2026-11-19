// <copyright file="BotLootAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// What a bot does with an item.
/// </summary>
internal enum BotLootAction
{
    /// <summary>The bot leaves the item alone; it is not loot it wants, or it is already worn.</summary>
    Ignore = 0,

    /// <summary>The bot keeps the item for later use, e.g. potions or jewels within its stock.</summary>
    Keep = 1,

    /// <summary>The bot equips the item as an upgrade.</summary>
    Equip = 2,

    /// <summary>The bot sells the item to a merchant NPC.</summary>
    Sell = 3,

    /// <summary>The bot puts the item into its personal store for the player market.</summary>
    Store = 4,
}
