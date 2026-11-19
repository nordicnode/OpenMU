// <copyright file="BotPartyGoal.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Why a bot party exists. The goal decides which bots belong together, which roles and gear they need
/// and when the group is rebalanced, so a party is more than an arbitrary bundle of nearby characters.
/// </summary>
internal enum BotPartyGoal
{
    /// <summary>
    /// Farming experience at a hunting ground. This is the default and needs a buffer for the group.
    /// </summary>
    Experience,

    /// <summary>
    /// Waiting at an opened mini-game event and joining it together when a player takes the group in.
    /// </summary>
    EventRun,

    /// <summary>
    /// Heading to an invasion to fight the event's bosses and champions.
    /// </summary>
    BossAttempt,

    /// <summary>
    /// Escorting a merchant bot to its stall on the market or in a city.
    /// </summary>
    MarketEscort,
}
