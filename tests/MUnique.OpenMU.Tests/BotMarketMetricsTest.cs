// <copyright file="BotMarketMetricsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.PlayerShopCurrencies;

/// <summary>
/// Tests the market counters which <c>/botmarket</c> shows: totals, the last hour and the price bands.
/// </summary>
[TestFixture]
public class BotMarketMetricsTest
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The totals survive the last hour; the hourly counters only cover the last sixty minutes.
    /// </summary>
    [Test]
    public void TotalsAndLastHourAreCountedSeparately()
    {
        var metrics = new BotMarketMetrics();
        metrics.RecordListed(BotMerchandise.PriceClass.Cheap, new ShopPrice(1_000_000), Now.AddMinutes(-90));
        metrics.RecordSold(Now.AddMinutes(-90));
        metrics.RecordRetired(2, Now.AddMinutes(-90));
        metrics.RecordIncome(500_000, 1, Now.AddMinutes(-90));

        metrics.RecordListed(BotMerchandise.PriceClass.Cheap, new ShopPrice(2_000_000), Now);
        metrics.RecordSold(Now);
        metrics.RecordRetired(1, Now);
        metrics.RecordIncome(1_500_000, 0, Now);

        var statistics = metrics.GetStatistics(Now);

        Assert.That(statistics.ItemsListed, Is.EqualTo(2));
        Assert.That(statistics.ItemsSold, Is.EqualTo(2));
        Assert.That(statistics.ItemsRetired, Is.EqualTo(3));
        Assert.That(statistics.ZenInflow, Is.EqualTo(2_000_000));
        Assert.That(statistics.PaymentItems, Is.EqualTo(1));
        Assert.That(statistics.LastHour, Is.EqualTo(new BotMarketHourStatistics(1, 1, 1, 1_500_000)));
    }

    /// <summary>
    /// A minute exactly an hour ago is outside the window; a minute later is inside it.
    /// </summary>
    [Test]
    public void LastHourStartsSixtyMinutesAgo()
    {
        var metrics = new BotMarketMetrics();
        metrics.RecordSold(Now.AddMinutes(-60));
        metrics.RecordSold(Now.AddMinutes(-59));

        Assert.That(metrics.GetStatistics(Now).LastHour.ItemsSold, Is.EqualTo(1));
    }

    /// <summary>
    /// A bucket which is reused after an hour doesn't keep the counts of the previous hour.
    /// </summary>
    [Test]
    public void ReusedBucketIsReset()
    {
        var metrics = new BotMarketMetrics();
        metrics.RecordSold(Now.AddHours(-1));
        metrics.RecordSold(Now);

        Assert.That(metrics.GetStatistics(Now).ItemsSold, Is.EqualTo(2));
        Assert.That(metrics.GetStatistics(Now).LastHour.ItemsSold, Is.EqualTo(1));
    }

    /// <summary>
    /// Each price class has its own zen band; jewel prices are counted as listed but don't distort it.
    /// </summary>
    [Test]
    public void PriceBandsShowZenPricesPerClass()
    {
        var metrics = new BotMarketMetrics();
        var jewel = new ShopPrice(3, new Mock<ItemDefinition>().Object);

        metrics.RecordListed(BotMerchandise.PriceClass.Cheap, new ShopPrice(1_000_000), Now);
        metrics.RecordListed(BotMerchandise.PriceClass.Cheap, new ShopPrice(3_000_000), Now.AddSeconds(1));
        metrics.RecordListed(BotMerchandise.PriceClass.Cheap, jewel, Now.AddSeconds(2));
        metrics.RecordListed(BotMerchandise.PriceClass.Medium, new ShopPrice(5_000_000), Now.AddSeconds(3));

        var bands = metrics.GetStatistics(Now).PriceBands;

        Assert.That(bands[BotMerchandise.PriceClass.Cheap].Listed, Is.EqualTo(3));
        Assert.That(bands[BotMerchandise.PriceClass.Cheap].ZenListed, Is.EqualTo(2));
        Assert.That(bands[BotMerchandise.PriceClass.Cheap].MinimumZen, Is.EqualTo(1_000_000));
        Assert.That(bands[BotMerchandise.PriceClass.Cheap].MaximumZen, Is.EqualTo(3_000_000));
        Assert.That(bands[BotMerchandise.PriceClass.Cheap].AverageZen, Is.EqualTo(2_000_000));
        Assert.That(bands[BotMerchandise.PriceClass.Medium].Listed, Is.EqualTo(1));
        Assert.That(bands[BotMerchandise.PriceClass.Jewel].Listed, Is.Zero);
    }
}
