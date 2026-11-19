// <copyright file="BotMarketStatistics.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// What the bot market did on one game server since the <see cref="BotMerchantPlugIn"/> started: how many
/// goods were listed, sold and retired, how much currency the bots took in, and the prices of the goods
/// of each class. A game master reads it with <c>/botmarket</c> to tell a lively market from one which
/// its guardrails hold back.
/// </summary>
/// <param name="ItemsListed">The number of goods which were put into a store with a price.</param>
/// <param name="ItemsSold">The number of goods which a player bought from a bot.</param>
/// <param name="ItemsRetired">The number of goods which left the stores without a buyer.</param>
/// <param name="ZenInflow">The zen which players paid to bots; the bots don't keep it.</param>
/// <param name="PaymentItems">The number of jewels and other items which players paid to bots.</param>
/// <param name="LastHour">The same counters for the last sixty minutes.</param>
/// <param name="PriceBands">The listed goods and their zen prices, per price class.</param>
internal sealed record BotMarketStatistics(
    long ItemsListed,
    long ItemsSold,
    long ItemsRetired,
    long ZenInflow,
    long PaymentItems,
    BotMarketHourStatistics LastHour,
    IReadOnlyDictionary<BotMerchandise.PriceClass, BotMarketPriceBand> PriceBands);
