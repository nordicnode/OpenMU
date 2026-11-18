// <copyright file="BotChatValues.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Builds the placeholder values of a bot's chat line, which the <see cref="BotChatComposer"/> renders
/// in the receiver's language. Pure logic - exposed for unit tests.
/// </summary>
internal static class BotChatValues
{
    /// <summary>
    /// Creates the placeholder values which describe the bot itself: its level, class, map, location,
    /// wings and store name.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The values, keyed by placeholder name.</returns>
    internal static Dictionary<string, Func<CultureInfo, string>> Create(Player bot)
    {
        var level = ((int)(bot.Attributes?[Stats.Level] ?? 0)).ToString(CultureInfo.InvariantCulture);
        var map = bot.CurrentMap?.Definition.Name;
        var position = bot.Position;
        var characterClass = bot.SelectedCharacter?.CharacterClass?.Name;
        return new Dictionary<string, Func<CultureInfo, string>>
        {
            ["level"] = _ => level,
            ["class"] = culture => characterClass?.GetTranslation(culture) ?? string.Empty,
            ["map"] = culture => map?.GetTranslation(culture) ?? string.Empty,
            ["location"] = culture => BotChatComposer.FormatLocation(map?.GetTranslation(culture), position),
            ["wings"] = GetWingsText(GetWings(bot)),
            ["store"] = _ => (bot as OfflinePlayer)?.MarketShift?.StoreName ?? string.Empty,
        };
    }

    /// <summary>
    /// Gets the wings the player wears.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The wings, or <c>null</c> when none are equipped.</returns>
    internal static Item? GetWings(Player player) => player.Inventory?.GetItem(InventoryConstants.WingsSlot);

    /// <summary>
    /// Gets the <c>{wings}</c> value of a pair of wings, e.g. "Wings of Soul +9" - in the receiver's
    /// language; empty without wings.
    /// </summary>
    /// <param name="wings">The wings.</param>
    /// <returns>The value of the placeholder.</returns>
    internal static Func<CultureInfo, string> GetWingsText(Item? wings)
    {
        var name = wings?.Definition?.Name;
        var level = wings?.Level ?? 0;
        return culture => name is { } itemName ? BotChatComposer.FormatItem(itemName.GetTranslation(culture), level) : string.Empty;
    }
}
