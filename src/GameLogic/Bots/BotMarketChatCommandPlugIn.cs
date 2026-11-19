// <copyright file="BotMarketChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A chat command for game masters which shows what the bot market of the game server is doing:
/// <c>/botmarket</c> lists the open stores and their goods, the goods listed, sold and retired in the
/// last hour and since the plugin started, the zen and items which the bots took in, the price band of
/// each class and the configured guardrails.
/// </summary>
/// <remarks>
/// The counters are in memory only; a restart of the game server starts them over. The open stores are
/// counted live, so they can differ from the counters of the last shift which ended a moment ago.
/// </remarks>
[PlugIn]
[Display(Name = nameof(PlugInResources.BotMarketChatCommandPlugIn_Name), Description = nameof(PlugInResources.BotMarketChatCommandPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("3F6D2B8A-7E41-4C9D-A5B2-1E8C6F0D4A37")]
[ChatCommandHelp(CommandKey, CharacterStatus.GameMaster)]
public class BotMarketChatCommandPlugIn : IChatCommandPlugIn
{
    private const string CommandKey = "/botmarket";

    /// <inheritdoc />
    public string Key => CommandKey;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.GameMaster;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        if (player.GameContext.FeaturePlugIns.GetPlugIn<BotMerchantPlugIn>() is not { } merchantPlugIn)
        {
            await player.ShowBlueMessageAsync("The bot merchants plugin is not active on this game server.").ConfigureAwait(false);
            return;
        }

        var players = await player.GameContext.GetPlayersAsync().ConfigureAwait(false);
        var merchants = players
            .OfType<OfflinePlayer>()
            .Where(p => p.Account?.IsBot == true && (p.ShopStorage?.StoreOpen ?? false))
            .ToList();
        var listed = merchants.Sum(b => BotMarketPlanner.CountPricedGoods(b));
        var statistics = merchantPlugIn.GetStatistics(player.GameContext, DateTime.UtcNow);

        await player.ShowBlueMessageAsync("Bot market of this game server:").ConfigureAwait(false);
        await player.ShowBlueMessageAsync($"  {merchants.Count} store(s) open with {listed} priced good(s).").ConfigureAwait(false);
        await player.ShowBlueMessageAsync(
            $"  last hour: {statistics.LastHour.ItemsListed} listed, {statistics.LastHour.ItemsSold} sold, {statistics.LastHour.ItemsRetired} retired, {Format(statistics.LastHour.ZenInflow)} zen taken in.").ConfigureAwait(false);
        await player.ShowBlueMessageAsync(
            $"  since start: {statistics.ItemsListed} listed, {statistics.ItemsSold} sold, {statistics.ItemsRetired} retired, {Format(statistics.ZenInflow)} zen and {statistics.PaymentItems} paid item(s) taken in.").ConfigureAwait(false);

        foreach (var (priceClass, band) in statistics.PriceBands)
        {
            if (priceClass == BotMerchandise.PriceClass.None || band.Listed == 0)
            {
                continue;
            }

            var prices = band.ZenListed > 0
                ? $"{Format(band.MinimumZen)} - {Format(band.MaximumZen)}, average {Format((int)Math.Round(band.AverageZen, MidpointRounding.AwayFromZero))}"
                : "prices in jewels";
            await player.ShowBlueMessageAsync($"  {priceClass.ToString().ToLowerInvariant()}: {band.Listed} listed, {prices}.").ConfigureAwait(false);
        }

        if (merchantPlugIn.Configuration is { } configuration)
        {
            await player.ShowBlueMessageAsync(
                $"  guardrails: zen {Format(configuration.EffectiveMinimumZenPrice)} - {Format(configuration.EffectiveMaximumZenPrice)}, "
                + $"{configuration.MaximumGoodsPerStore} good(s) per store, {configuration.MaximumListedGoods} on the market, {configuration.MaximumMerchants} store(s).").ConfigureAwait(false);
        }
    }

    private static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
