// <copyright file="IBotMarketService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Shared.Services;

/// <summary>
/// Provides the market of the bot merchants of the in-process game servers.
/// </summary>
public interface IBotMarketService
{
    /// <summary>
    /// Gets the market of every in-process game server which has the bot merchants plugin active. Servers
    /// without the plugin are left out, so an empty result means that there is no market to show.
    /// </summary>
    /// <returns>The markets, one per server.</returns>
    Task<IReadOnlyList<BotMarketServerStatus>> GetMarketStatusesAsync();
}
