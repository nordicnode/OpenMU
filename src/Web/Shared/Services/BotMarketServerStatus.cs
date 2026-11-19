// <copyright file="BotMarketServerStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Shared.Services;

using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The market of one game server for the administration surfaces.
/// </summary>
/// <param name="ServerId">The identifier of the game server.</param>
/// <param name="ServerDescription">The description of the game server.</param>
/// <param name="Market">The market of the server.</param>
public sealed record BotMarketServerStatus(
    byte ServerId,
    string ServerDescription,
    BotMarketStatus Market);
