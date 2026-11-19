// <copyright file="BotMarketClassStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The listed goods of one price class and the band of their zen prices, for the administration surfaces.
/// </summary>
/// <param name="Name">The name of the price class, e.g. <c>Cheap</c>.</param>
/// <param name="Listed">The number of listed goods of the class.</param>
/// <param name="ZenListed">The number of them which are priced in zen.</param>
/// <param name="MinimumZen">The lowest zen price; 0 without zen prices.</param>
/// <param name="MaximumZen">The highest zen price; 0 without zen prices.</param>
/// <param name="AverageZen">The average zen price; 0 without zen prices.</param>
public sealed record BotMarketClassStatus(
    string Name,
    long Listed,
    long ZenListed,
    int MinimumZen,
    int MaximumZen,
    double AverageZen);
