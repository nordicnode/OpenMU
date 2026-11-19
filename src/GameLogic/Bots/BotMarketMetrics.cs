// <copyright file="BotMarketMetrics.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.PlayerShopCurrencies;

/// <summary>
/// Collects what the bot market does for <see cref="BotMerchantPlugIn"/>: the goods listed, sold and
/// retired, the currency taken in, and the zen prices per class. The counters live in memory only; a
/// restart starts them over, like the unsold counts of the goods.
/// </summary>
/// <remarks>
/// Stocking, sales and income removal can run on different threads of a game server, so the counters
/// are guarded by a short lock. The last hour is kept in one bucket per minute, so reading it does not
/// accumulate an unbounded history.
/// </remarks>
internal sealed class BotMarketMetrics
{
    private const int MinutesPerHour = 60;
    private const int PriceClassCount = (int)BotMerchandise.PriceClass.Jewel + 1;

    private readonly object _sync = new();
    private readonly MinuteBucket[] _buckets = new MinuteBucket[MinutesPerHour];
    private readonly PriceBand[] _bands = new PriceBand[PriceClassCount];

    private long _itemsListed;
    private long _itemsSold;
    private long _itemsRetired;
    private long _zenInflow;
    private long _paymentItems;

    /// <summary>
    /// Counts a good which was put into a store with a price.
    /// </summary>
    /// <param name="priceClass">The price class of the good.</param>
    /// <param name="price">The price of the good; <c>null</c> if it isn't known.</param>
    /// <param name="now">The current time.</param>
    public void RecordListed(BotMerchandise.PriceClass priceClass, ShopPrice? price, DateTime now)
    {
        lock (this._sync)
        {
            this._itemsListed++;
            this.GetBucket(now).ItemsListed++;

            var index = (int)priceClass;
            if (index is < 0 or >= PriceClassCount)
            {
                return;
            }

            ref var band = ref this._bands[index];
            band.Listed++;
            if (price is { Kind: ShopPriceKind.Zen, Amount: > 0 } zen)
            {
                band.ZenListed++;
                band.ZenSum += zen.Amount;
                band.MinimumZen = band.ZenListed == 1 ? zen.Amount : Math.Min(band.MinimumZen, zen.Amount);
                band.MaximumZen = Math.Max(band.MaximumZen, zen.Amount);
            }
        }
    }

    /// <summary>
    /// Counts a good which a player bought from a bot.
    /// </summary>
    /// <param name="now">The current time.</param>
    public void RecordSold(DateTime now)
    {
        lock (this._sync)
        {
            this._itemsSold++;
            this.GetBucket(now).ItemsSold++;
        }
    }

    /// <summary>
    /// Counts the currency which the bots took in and which is removed from them right after.
    /// </summary>
    /// <param name="zen">The zen which the players paid.</param>
    /// <param name="items">The number of jewels and other items which the players paid.</param>
    /// <param name="now">The current time.</param>
    public void RecordIncome(int zen, int items, DateTime now)
    {
        lock (this._sync)
        {
            this._zenInflow += Math.Max(0, zen);
            this._paymentItems += Math.Max(0, items);
            this.GetBucket(now).ZenInflow += Math.Max(0, zen);
        }
    }

    /// <summary>
    /// Counts goods which left the stores without a buyer.
    /// </summary>
    /// <param name="count">The number of goods.</param>
    /// <param name="now">The current time.</param>
    public void RecordRetired(int count, DateTime now)
    {
        if (count <= 0)
        {
            return;
        }

        lock (this._sync)
        {
            this._itemsRetired += count;
            this.GetBucket(now).ItemsRetired += count;
        }
    }

    /// <summary>
    /// Gets the counters since the plugin started and the ones of the last sixty minutes.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The statistics.</returns>
    public BotMarketStatistics GetStatistics(DateTime now)
    {
        lock (this._sync)
        {
            var currentMinute = GetMinute(now);
            var oldestMinute = currentMinute - (MinutesPerHour - 1);
            var hour = new BotMarketHourStatistics(0, 0, 0, 0);
            foreach (var bucket in this._buckets)
            {
                if (bucket.Minute < oldestMinute || bucket.Minute > currentMinute)
                {
                    continue;
                }

                hour = new BotMarketHourStatistics(
                    hour.ItemsListed + bucket.ItemsListed,
                    hour.ItemsSold + bucket.ItemsSold,
                    hour.ItemsRetired + bucket.ItemsRetired,
                    hour.ZenInflow + bucket.ZenInflow);
            }

            var bands = new Dictionary<BotMerchandise.PriceClass, BotMarketPriceBand>(PriceClassCount);
            for (var i = 0; i < PriceClassCount; i++)
            {
                var band = this._bands[i];
                bands[(BotMerchandise.PriceClass)i] = new BotMarketPriceBand(
                    band.Listed,
                    band.ZenListed,
                    band.ZenListed > 0 ? band.MinimumZen : 0,
                    band.MaximumZen,
                    band.ZenListed > 0 ? (double)band.ZenSum / band.ZenListed : 0);
            }

            return new BotMarketStatistics(
                this._itemsListed,
                this._itemsSold,
                this._itemsRetired,
                this._zenInflow,
                this._paymentItems,
                hour,
                bands);
        }
    }

    private static long GetMinute(DateTime now) => now.Ticks / TimeSpan.TicksPerMinute;

    private ref MinuteBucket GetBucket(DateTime now)
    {
        var minute = GetMinute(now);
        ref var bucket = ref this._buckets[(int)(minute % MinutesPerHour)];
        if (bucket.Minute != minute)
        {
            bucket = new MinuteBucket { Minute = minute };
        }

        return ref bucket;
    }

    /// <summary>
    /// The counters of one minute of the last hour. The minute tells whether the bucket was used before,
    /// so a bucket from the previous hour is reset instead of being added to.
    /// </summary>
    private struct MinuteBucket
    {
        /// <summary>The minute since the epoch, or 0 for an unused bucket.</summary>
        public long Minute;

        /// <summary>The goods listed in the minute.</summary>
        public long ItemsListed;

        /// <summary>The goods sold in the minute.</summary>
        public long ItemsSold;

        /// <summary>The goods retired in the minute.</summary>
        public long ItemsRetired;

        /// <summary>The zen taken in during the minute.</summary>
        public long ZenInflow;
    }

    /// <summary>
    /// The counters and the zen price band of one price class.
    /// </summary>
    private struct PriceBand
    {
        /// <summary>The number of listed goods of the class.</summary>
        public long Listed;

        /// <summary>The number of them which are priced in zen.</summary>
        public long ZenListed;

        /// <summary>The sum of their zen prices.</summary>
        public long ZenSum;

        /// <summary>The lowest zen price.</summary>
        public int MinimumZen;

        /// <summary>The highest zen price.</summary>
        public int MaximumZen;
    }
}
