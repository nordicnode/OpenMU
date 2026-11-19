// <copyright file="BotMerchantPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerShopCurrencies;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Lets the server-side bots sell their goods in personal stores on the market map (Loren Market) and,
/// for a share of the shifts, in the cities: a bot with enough priced goods in its store goes there for
/// a shift, opens the store and goes back hunting afterward (see <see cref="BotMarketShift"/>).
/// </summary>
/// <remarks>
/// Every bot may become a merchant now and then - there are no dedicated merchant bots. A shift ends
/// when its time is up, when the bot sold everything, after a long time without a sale and when
/// the bot logs out (the presence rotation of the bots goes on as usual). The limits of the
/// <see cref="BotMerchantConfiguration"/> apply per game server.
/// It's a feature plugin of the bots, listed in the admin panel next to them.
/// </remarks>
[PlugIn]
[Display(Name = nameof(PlugInResources.BotMerchantPlugIn_Name), Description = nameof(PlugInResources.BotMerchantPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("6E0B6C2F-8F7E-4C71-9C55-2B7C1E4A9D13")]
public class BotMerchantPlugIn
    : IFeaturePlugIn,
    IPeriodicTaskPlugIn,
    IItemSoldToOtherPlayerPlugIn,
    ISupportCustomConfiguration<BotMerchantConfiguration>,
    ISupportDefaultCustomConfiguration,
    IDisabledByDefault
{
    /// <summary>
    /// How often a game server sends new bots to the market. The shifts last half an hour and more, so
    /// there's no need to check every second like the timer of the periodic tasks does.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the counts of the goods which weren't counted for a long time are forgotten.
    /// </summary>
    private static readonly TimeSpan UnsoldShiftsCleanupInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The least time for which the count of a good is kept after its last shift. A good leaves the
    /// stores without a sale too - a retired good goes to an NPC, the bot may be removed -, so without
    /// it the counts would pile up until the server restarts.
    /// </summary>
    private static readonly TimeSpan MinimumUnsoldShiftsMemory = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<IGameContext, DateTime> _nextCheckUtc = new();

    private readonly ConcurrentDictionary<IGameContext, BotMarketMetrics> _metrics = new();

    /// <summary>
    /// The number of market shifts each good spent in a store without a buyer, and when it was counted
    /// the last time, by the id of the item. In memory only: a restart of the server gives every good a
    /// new chance, like a count which is forgotten after a long time (see <see cref="ForgetOldUnsoldShifts"/>).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, UnsoldGood> _unsoldShifts = new();

    private long _nextUnsoldShiftsCleanupTicks;

    /// <inheritdoc />
    public BotMerchantConfiguration? Configuration { get; set; }

    /// <summary>
    /// Gets the number of goods whose shifts without a buyer are counted, old ones included until they're forgotten.
    /// </summary>
    internal int UnsoldGoodCount => this._unsoldShifts.Count;

    /// <inheritdoc />
    public object CreateDefaultConfig() => new BotMerchantConfiguration();

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        var now = DateTime.UtcNow;
        if (this.Configuration is not { } configuration
            || now < this._nextCheckUtc.GetValueOrDefault(gameContext))
        {
            return;
        }

        this._nextCheckUtc[gameContext] = now + CheckInterval;
        this.ForgetOldUnsoldShifts(configuration, now);
        await this.StartShiftsAsync(gameContext, configuration, now).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ForceStart() => this._nextCheckUtc.Clear();

    /// <inheritdoc />
    public void ItemSold(Player seller, Item item, Player buyer)
    {
        if (seller is OfflinePlayer { MarketShift: { } shift } bot)
        {
            shift.LastSaleUtc = DateTime.UtcNow;
            this._unsoldShifts.TryRemove(item.GetId(), out _);
            bot.GameContext.FeaturePlugIns.GetPlugIn<BotMerchantPlugIn>()?.RecordSold(bot.GameContext, DateTime.UtcNow);

            // The bot buys nothing, so its income is removed - in its own tick, which is the only
            // place allowed to change its inventory (see OfflinePlayer.PendingBotActions).
            bot.PendingBotActions.Enqueue(() => BotMerchandise.RemoveIncomeAsync(bot, shift));
        }
    }

    /// <summary>
    /// Gets the market of one game server in the shape the administration surfaces show it.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The snapshot, or <c>null</c> when the plugin has no configuration.</returns>
    public async ValueTask<BotMarketStatus?> GetMarketStatusAsync(IGameContext gameContext, DateTime now)
    {
        if (this.Configuration is not { } configuration)
        {
            return null;
        }

        var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
        var merchants = players
            .OfType<OfflinePlayer>()
            .Where(p => p.Account?.IsBot == true && (p.ShopStorage?.StoreOpen ?? false))
            .ToList();
        var listedGoods = merchants.Sum(b => BotMarketPlanner.CountPricedGoods(b));
        var statistics = this.GetStatistics(gameContext, now);
        var priceClasses = statistics.PriceBands
            .Where(entry => entry.Key != BotMerchandise.PriceClass.None && entry.Value.Listed > 0)
            .OrderBy(entry => entry.Key)
            .Select(entry => new BotMarketClassStatus(
                entry.Key.ToString(),
                entry.Value.Listed,
                entry.Value.ZenListed,
                entry.Value.MinimumZen,
                entry.Value.MaximumZen,
                entry.Value.AverageZen))
            .ToList();

        return new BotMarketStatus(
            merchants.Count,
            listedGoods,
            statistics.LastHour.ItemsListed,
            statistics.LastHour.ItemsSold,
            statistics.LastHour.ItemsRetired,
            statistics.LastHour.ZenInflow,
            statistics.ItemsListed,
            statistics.ItemsSold,
            statistics.ItemsRetired,
            statistics.ZenInflow,
            statistics.PaymentItems,
            priceClasses,
            configuration.EffectiveMinimumZenPrice,
            configuration.EffectiveMaximumZenPrice,
            configuration.MaximumGoodsPerStore,
            configuration.MaximumListedGoods,
            configuration.MaximumMerchants);
    }

    /// <summary>
    /// Takes the bot out of its party of bots for the shift (see <see cref="BotMarketPlanner.MayStartShift"/>).
    /// </summary>
    /// <remarks>
    /// A player may have joined the party since the bot was picked, so the members are checked again
    /// right before - a party with a player is never left.
    /// </remarks>
    /// <param name="bot">The bot.</param>
    /// <returns><c>true</c>, if the bot is in no party anymore.</returns>
    internal static async ValueTask<bool> LeavePartyAsync(OfflinePlayer bot)
    {
        if (bot.Party is not { } party)
        {
            return true;
        }

        if (!BotMarketPlanner.HasOnlyBots(party.PartyList))
        {
            return false;
        }

        try
        {
            await party.KickMySelfAsync(bot).ConfigureAwait(false);
            bot.Logger.LogDebug("Bot '{Name}' leaves its party for a market shift.", bot.Name);
        }
        catch (Exception ex)
        {
            bot.Logger.LogWarning(ex, "Bot '{Name}' couldn't leave its party for a market shift.", bot.Name);
        }

        return bot.Party is null;
    }

    /// <summary>
    /// Gets the statistics of the market of this game server, for the <c>/botmarket</c> chat command.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The statistics since the plugin started and of the last hour.</returns>
    internal BotMarketStatistics GetStatistics(IGameContext gameContext, DateTime now)
        => this._metrics.GetOrAdd(gameContext, _ => new BotMarketMetrics()).GetStatistics(now);

    /// <summary>
    /// Counts a good which was put into a store with a price.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="priceClass">The price class of the good.</param>
    /// <param name="price">The price of the good.</param>
    /// <param name="now">The current time.</param>
    internal void RecordListed(IGameContext gameContext, BotMerchandise.PriceClass priceClass, ShopPrice? price, DateTime now)
        => this._metrics.GetOrAdd(gameContext, _ => new BotMarketMetrics()).RecordListed(priceClass, price, now);

    /// <summary>
    /// Counts a good which a player bought from a bot.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="now">The current time.</param>
    internal void RecordSold(IGameContext gameContext, DateTime now)
        => this._metrics.GetOrAdd(gameContext, _ => new BotMarketMetrics()).RecordSold(now);

    /// <summary>
    /// Counts the currency which a bot took in and which is removed from it right after.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="zen">The zen which the player paid.</param>
    /// <param name="items">The number of jewels and other items which the player paid.</param>
    /// <param name="now">The current time.</param>
    internal void RecordIncome(IGameContext gameContext, int zen, int items, DateTime now)
        => this._metrics.GetOrAdd(gameContext, _ => new BotMarketMetrics()).RecordIncome(zen, items, now);

    /// <summary>
    /// Counts goods which left the stores without a buyer.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="count">The number of goods.</param>
    /// <param name="now">The current time.</param>
    internal void RecordRetired(IGameContext gameContext, int count, DateTime now)
        => this._metrics.GetOrAdd(gameContext, _ => new BotMarketMetrics()).RecordRetired(count, now);

    /// <summary>
    /// Counts one more market shift without a buyer for the good.
    /// </summary>
    /// <param name="item">The good.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The number of shifts the good spent in the store without a buyer.</returns>
    internal int CountUnsoldShift(Item item, DateTime now)
        => this._unsoldShifts.AddOrUpdate(item.GetId(), new UnsoldGood(1, now), (_, good) => new UnsoldGood(good.Shifts + 1, now)).Shifts;

    /// <summary>
    /// Determines whether the good spent too many shifts in a store without a buyer: it's sold to an NPC
    /// and doesn't go back into the store.
    /// </summary>
    /// <param name="item">The good.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns><c>true</c>, if the good is retired from the market.</returns>
    internal bool IsRetired(Item item, BotMerchantConfiguration configuration)
        => this._unsoldShifts.TryGetValue(item.GetId(), out var good) && good.Shifts >= configuration.UnsoldShiftsUntilNpc;

    /// <summary>
    /// Forgets the counts of the goods which weren't counted for a long time: they left the stores without
    /// a sale, or their bot didn't go to the market for so long that the good gets a new chance.
    /// </summary>
    /// <remarks>
    /// A good in a store is counted after each of its shifts, and a retired good is sold at the next visit
    /// of a merchant NPC, so the memory lasts several times as long as a shift and the break after it.
    /// Runs at most every <see cref="UnsoldShiftsCleanupInterval"/>.
    /// </remarks>
    /// <param name="configuration">The configuration.</param>
    /// <param name="now">The current time.</param>
    internal void ForgetOldUnsoldShifts(BotMerchantConfiguration configuration, DateTime now)
    {
        var next = Interlocked.Read(ref this._nextUnsoldShiftsCleanupTicks);
        if (now.Ticks < next
            || Interlocked.CompareExchange(ref this._nextUnsoldShiftsCleanupTicks, (now + UnsoldShiftsCleanupInterval).Ticks, next) != next)
        {
            return;
        }

        var memory = (configuration.MaximumShiftDuration + configuration.TimeBetweenShifts) * Math.Max(1, configuration.UnsoldShiftsUntilNpc + 1);
        var forgetBefore = now - (memory > MinimumUnsoldShiftsMemory ? memory : MinimumUnsoldShiftsMemory);
        foreach (var good in this._unsoldShifts)
        {
            if (good.Value.LastCountUtc < forgetBefore)
            {
                // Removes the entry only if it wasn't counted again meanwhile.
                this._unsoldShifts.TryRemove(good);
            }
        }
    }

    /// <summary>
    /// Sends bots with goods to the market and the cities until there's the configured number of merchants.
    /// </summary>
    private async ValueTask StartShiftsAsync(GameContext gameContext, BotMerchantConfiguration configuration, DateTime now)
    {
        var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
        var bots = players.OfType<OfflinePlayer>().Where(p => p.Account?.IsBot == true).ToList();
        var shifts = bots.Select(b => b.MarketShift).OfType<BotMarketShift>().ToList();
        var freePlaces = configuration.MaximumMerchants - shifts.Count;
        if (freePlaces <= 0)
        {
            return;
        }

        // The goods of the current shifts count against the market's quota, so the market doesn't grow
        // beyond what the configuration allows even when every merchant brings a full store.
        var listedGoods = bots.Where(b => b.MarketShift is not null).Sum(b => BotMarketPlanner.CountPricedGoods(b));

        var candidates = bots.Where(b => BotMarketPlanner.MayStartShift(b, configuration, now)).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var logger = gameContext.LoggerFactory.CreateLogger<BotMerchantPlugIn>();

        // Resolved once per check; a map which isn't usable (unknown, without a spawn gate) or full is null.
        var places = new Dictionary<short, MarketPlace?>();
        async ValueTask<MarketPlace?> GetPlaceAsync(short mapNumber)
        {
            if (!places.TryGetValue(mapNumber, out var place))
            {
                place = await MarketPlace.ResolveAsync(gameContext, mapNumber, shifts).ConfigureAwait(false);
                places[mapNumber] = place;
            }

            return place;
        }

        // The market is made for stores, so its whole safezone is used; in a city, the stores stay in the middle.
        async ValueTask<(MarketPlace? Place, Point? Stall)> FindStallAsync(short mapNumber)
        {
            if (await GetPlaceAsync(mapNumber).ConfigureAwait(false) is not { } place)
            {
                return (null, null);
            }

            var searchRange = mapNumber == configuration.MarketMapNumber ? (int?)null : BotMarketPlanner.StallSearchRange;
            var stall = BotMarketPlanner.FindStall(place.Map.Terrain, place.Arrival, place.Stalls, Rand.NextInt, searchRange);
            if (stall is null)
            {
                logger.LogDebug("The map {Map} has no free stall for more bots.", place.Definition.Name);
                places[mapNumber] = null;
            }

            return (place, stall);
        }

        var cities = configuration.GetCityMapNumbers().Where(n => n != configuration.MarketMapNumber).ToList();
        var storeNames = configuration.GetStoreNames();
        var takenNames = shifts.Select(s => s.StoreName).ToList();

        // Bots without a party first, so that the parties of the bots lose as few members as possible.
        var starting = candidates
            .OrderBy(b => b.Party is null ? 0 : 1)
            .ThenBy(_ => Rand.NextInt(0, int.MaxValue))
            .Take(Math.Min(freePlaces, configuration.NewMerchantsPerCheck));
        foreach (var bot in starting)
        {
            var goods = BotMarketPlanner.CountPricedGoods(bot);
            if (!BotMarketPlanner.HasRoomForListing(listedGoods, goods, configuration.MaximumListedGoods))
            {
                continue;
            }

            var mapNumber = BotMarketPlanner.DrawShiftMap(configuration, cities, Rand.NextInt);

            // A city which can't take the bot (unusable or full) leaves it to the market.
            var (place, stall) = await FindStallAsync(mapNumber).ConfigureAwait(false);
            if (stall is null && mapNumber != configuration.MarketMapNumber)
            {
                (place, stall) = await FindStallAsync(configuration.MarketMapNumber).ConfigureAwait(false);
            }

            if (place is null || stall is not { } freeStall)
            {
                continue;
            }

            // The bot was picked before the awaits above; meanwhile, it may have started an errand,
            // joined an event meeting or a party with a player.
            if (!BotMarketPlanner.MayStartShift(bot, configuration, now)
                || !await LeavePartyAsync(bot).ConfigureAwait(false))
            {
                continue;
            }

            place.Stalls.Add(freeStall);
            listedGoods += goods;
            var storeName = BotMarketPlanner.DrawStoreName(storeNames, takenNames, bot.Name, Rand.NextInt);
            takenNames.Add(storeName);
            var duration = BotMarketPlanner.DrawShiftDuration(configuration, Rand.NextInt);
            bot.MarketShift = new BotMarketShift(place.Definition, freeStall, storeName, now, now + duration);
            bot.Logger.LogDebug("Bot '{Name}' goes to {Map} for {Duration} with {Count} goods.", bot.Name, place.Definition.Name, duration, BotMarketPlanner.CountPricedGoods(bot));
        }
    }

    /// <summary>
    /// The shifts which a good spent in a store without a buyer.
    /// </summary>
    /// <param name="Shifts">The number of shifts.</param>
    /// <param name="LastCountUtc">When the last shift was counted.</param>
    private readonly record struct UnsoldGood(int Shifts, DateTime LastCountUtc);

    /// <summary>
    /// A map on which the bots open their stores, with the stalls which are taken there.
    /// </summary>
    /// <param name="Definition">The definition of the map.</param>
    /// <param name="Map">The map instance of the game server.</param>
    /// <param name="Arrival">The center of the spawn gate, around which the stalls are.</param>
    /// <param name="Stalls">The taken stalls of this map.</param>
    private sealed record MarketPlace(GameMapDefinition Definition, GameMap Map, Point Arrival, List<Point> Stalls)
    {
        /// <summary>
        /// Resolves the map; <c>null</c>, if it's unknown or has no spawn gate the bots could arrive at.
        /// </summary>
        public static async ValueTask<MarketPlace?> ResolveAsync(GameContext gameContext, short mapNumber, IEnumerable<BotMarketShift> shifts)
        {
            if (gameContext.Configuration.Maps.FirstOrDefault(m => m.Number == mapNumber) is not { } definition
                || definition.ExitGates.FirstOrDefault(g => g.IsSpawnGate) is not { } gate
                || await gameContext.GetMapAsync((ushort)definition.Number).ConfigureAwait(false) is not { } map)
            {
                return null;
            }

            var arrival = new Point((byte)((gate.X1 + gate.X2) / 2), (byte)((gate.Y1 + gate.Y2) / 2));
            var stalls = shifts.Where(s => s.Map.Number == mapNumber).Select(s => s.Stall).ToList();
            return new MarketPlace(definition, map, arrival, stalls);
        }
    }
}
