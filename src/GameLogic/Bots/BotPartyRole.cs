// <copyright file="BotPartyRole.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The job a bot brings into a goal-driven party. Readiness checks use it to keep a group from being
/// all buffers or all glass cannons.
/// </summary>
internal enum BotPartyRole
{
    /// <summary>
    /// The energy elf which keeps the group alive and buffed.
    /// </summary>
    Support,

    /// <summary>
    /// A melee class which takes the front of a boss attempt or an escort.
    /// </summary>
    Vanguard,

    /// <summary>
    /// A ranged or magic class which deals damage from behind the vanguard.
    /// </summary>
    Damage,
}
