// <copyright file="BotMarketHourStatistics.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The market counters of the last sixty minutes.
/// </summary>
/// <param name="ItemsListed">The number of goods which were put into a store with a price.</param>
/// <param name="ItemsSold">The number of goods which a player bought from a bot.</param>
/// <param name="ItemsRetired">The number of goods which left the stores without a buyer.</param>
/// <param name="ZenInflow">The zen which players paid to bots.</param>
internal readonly record struct BotMarketHourStatistics(
    long ItemsListed,
    long ItemsSold,
    long ItemsRetired,
    long ZenInflow);
