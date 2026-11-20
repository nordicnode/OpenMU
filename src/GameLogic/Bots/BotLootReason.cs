// <copyright file="BotLootReason.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Why a bot keeps, equips, sells or stores an item. The codes are stable enough to be used in
/// operator-facing debug logs and balancing notes.
/// </summary>
internal enum BotLootReason
{
    /// <summary>The item has no definition, so nothing can be decided about it.</summary>
    UnknownItem = 0,

    /// <summary>The item is currently equipped.</summary>
    Equipped = 1,

    /// <summary>The item is ammunition, which must not be sold while the bot uses a bow.</summary>
    Ammunition = 2,

    /// <summary>The merchant refuses the item, so it is left alone.</summary>
    Unsellable = 3,

    /// <summary>The item is a wing; wings are granted, never traded.</summary>
    Wing = 4,

    /// <summary>The item is a healing or mana potion the offline AI drinks.</summary>
    ConsumableStock = 5,

    /// <summary>The item is a usable jewel and the bot still has room in its stock of that kind.</summary>
    JewelStock = 6,

    /// <summary>The item is a usable jewel above the bot's stock of that kind.</summary>
    JewelSurplus = 7,

    /// <summary>The item is a jewel the bot can never spend.</summary>
    UnusableJewel = 8,

    /// <summary>The item is an orb or scroll teaching a skill the bot does not know yet.</summary>
    SkillItem = 9,

    /// <summary>The item beats what the bot wears and is worth equipping.</summary>
    Upgrade = 10,

    /// <summary>The item is worth a stall on the player market.</summary>
    MarketCandidate = 11,

    /// <summary>The item is only worth selling to a merchant NPC.</summary>
    VendorTrash = 12,

    /// <summary>The item is gear which the bot cannot or does not want to wear.</summary>
    NotAnUpgrade = 13,

    /// <summary>The item is picked up because the bot's helper configuration names it.</summary>
    ConfiguredExtra = 14,

    /// <summary>The item is needed for an active quest or is retained for a quest hand-in.</summary>
    QuestItem = 15,
}
