// <copyright file="BotMarketPriceBand.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The listed goods of one price class and the band of their zen prices.
/// </summary>
/// <param name="Listed">The number of listed goods of the class.</param>
/// <param name="ZenListed">The number of them which are priced in zen.</param>
/// <param name="MinimumZen">The lowest zen price of the class; 0 without zen prices.</param>
/// <param name="MaximumZen">The highest zen price of the class; 0 without zen prices.</param>
/// <param name="AverageZen">The average zen price of the class; 0 without zen prices.</param>
internal sealed record BotMarketPriceBand(
    long Listed,
    long ZenListed,
    int MinimumZen,
    int MaximumZen,
    double AverageZen);
