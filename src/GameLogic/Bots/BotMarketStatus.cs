// <copyright file="BotMarketStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The market of one game server in the shape the administration surfaces show it: the open stores and
/// their goods, the activity of the last hour and since the plugin started, the prices per class and the
/// guardrails which hold the market back.
/// </summary>
/// <param name="OpenStores">The number of bots with an open store.</param>
/// <param name="ListedGoods">The number of priced goods in those stores.</param>
/// <param name="ListedLastHour">The goods listed in the last sixty minutes.</param>
/// <param name="SoldLastHour">The goods sold in the last sixty minutes.</param>
/// <param name="RetiredLastHour">The goods retired without a buyer in the last sixty minutes.</param>
/// <param name="ZenLastHour">The zen taken in during the last sixty minutes.</param>
/// <param name="ListedTotal">The goods listed since the plugin started.</param>
/// <param name="SoldTotal">The goods sold since the plugin started.</param>
/// <param name="RetiredTotal">The goods retired without a buyer since the plugin started.</param>
/// <param name="ZenTotal">The zen taken in since the plugin started.</param>
/// <param name="PaidItemsTotal">The jewels and other items taken in since the plugin started.</param>
/// <param name="PriceClasses">The listed goods and their zen price band, per class which has goods.</param>
/// <param name="MinimumZenPrice">The configured lowest zen price.</param>
/// <param name="MaximumZenPrice">The configured highest zen price.</param>
/// <param name="MaximumGoodsPerStore">The configured maximum of goods per store.</param>
/// <param name="MaximumListedGoods">The configured maximum of goods on the market.</param>
/// <param name="MaximumMerchants">The configured maximum of open stores.</param>
public sealed record BotMarketStatus(
    int OpenStores,
    int ListedGoods,
    long ListedLastHour,
    long SoldLastHour,
    long RetiredLastHour,
    long ZenLastHour,
    long ListedTotal,
    long SoldTotal,
    long RetiredTotal,
    long ZenTotal,
    long PaidItemsTotal,
    IReadOnlyList<BotMarketClassStatus> PriceClasses,
    int MinimumZenPrice,
    int MaximumZenPrice,
    int MaximumGoodsPerStore,
    int MaximumListedGoods,
    int MaximumMerchants);
