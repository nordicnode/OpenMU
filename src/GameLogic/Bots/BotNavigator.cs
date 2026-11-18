// <copyright file="BotNavigator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.PlayerStore;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Drives a bot's high-level navigation. It picks a hunting ground that suits the bot's level from
/// the monster spawn areas of the current map and points the bot's <see cref="OfflinePlayer.HuntingOrigin"/>
/// at it. The existing offline movement handler then walks the bot there (using the server pathfinder)
/// and the combat handler hunts locally - so a bot which spawns in town finds its own way to the monsters.
/// </summary>
internal sealed class BotNavigator : AsyncDisposable
{
    /// <summary>Fallback when the server defines no warps: the lowest stock requirement.</summary>
    private const int FallbackMinWarpLevel = 10;

    /// <summary>Range (tiles) around the origin that counts as "at the hunting ground".</summary>
    private const int HuntingRange = BotMuHelperSettings.DefaultHuntingRange;

    /// <summary>
    /// Share of the shopping cooldown the next trip is spread over, so that bots which shopped together
    /// don't come back together. See <see cref="NextShoppingCheckUtc"/>.
    /// </summary>
    private const double ShoppingSpread = 0.4;

    /// <summary>
    /// Share of the buff check interval the next check is spread over, so bots don't all walk to the
    /// buff NPC at once. See <see cref="NextBuffCheckUtc"/>.
    /// </summary>
    private const double BuffSpread = 0.4;

    /// <summary>
    /// How far the bot looks for an actual live monster to home in on. MU spawn areas span most of the map
    /// (e.g. Lorencia's are ~86x233 / ~105x68 tiles) with only a few dozen monsters each, so their monsters
    /// sit ~20 tiles apart. Walking to a random tile inside such an area almost never lands within the 6-tile
    /// combat range of a real monster - the bot arrives, finds nothing to fight, and wanders off. So once the
    /// bot is anywhere in the region it scans this radius for the nearest live monster and heads straight for
    /// it. Kept modest so the per-tick area-of-interest scan stays cheap even with hundreds of bots.
    /// </summary>
    private const int MonsterSeekRadius = 40;

    /// <summary>
    /// How far around the bot's own tile a walkable one is looked for when judging which part of the map it
    /// stands in. A bot often lands on the blocked tile of a spawn gate, and the region of the tile next to it
    /// is the one it fights in (see <see cref="BotMapRegions.GetRegionNear"/>).
    /// </summary>
    private const int RegionSearchRadius = 3;

    /// <summary>Item group of potions (healing and mana potions share group 14).</summary>
    private const int HealPotionGroup = 14;

    /// <summary>Item number of the Large Healing Potion within <see cref="HealPotionGroup"/>.</summary>
    private const int HealPotionNumber = 3;

    /// <summary>Item number of the Large Mana Potion within <see cref="HealPotionGroup"/>.</summary>
    private const int ManaPotionNumber = 6;

    /// <summary>
    /// How many charges an emergency refill stocks up (the potion item's Durability holds the remaining
    /// count; one is spent per heal). Spread over as many item stacks as the definition's stack size
    /// needs: a Large Healing Potion holds three charges per stack, so a bot handed a single 255-charge
    /// "potion" carried an item the game cannot produce - its stack never looked low again (the shopping
    /// loop for that potion kind went dead), and stacking it with a bought one underflowed the count.
    /// </summary>
    private const int EmergencyPotionCharges = 21;

    /// <summary>
    /// Emergency refill threshold: normally the bot restocks potions by BUYING them at a merchant (see
    /// <see cref="BotShoppingHandler"/>, triggered well above this level); only when a stack still runs
    /// this low (e.g. no Zen, merchant unreachable) is it topped up out of thin air as a last resort,
    /// so the bot never dies over an empty bottle.
    /// </summary>
    private const int HealPotionRefillBelow = 10;

    /// <summary>Number of path steps to issue per travel hop. Short hops let the bot stop and fight between hops.</summary>
    private const int StepsPerHop = 3;

    /// <summary>
    /// How far the travel destination may drift (e.g. a roaming monster the bot homes in on) before the
    /// cached route is re-planned. Re-planning a whole-map A* every tick just to consume three steps
    /// starved the pathfinder pool once dozens of bots traveled at the same time.
    /// </summary>
    private const int PathReuseTolerance = 4;

    /// <summary>
    /// If a monster is within this range the bot stops traveling and lets the combat handler engage.
    /// MUST equal the combat hunting range: the combat handler only ever targets monsters within
    /// <see cref="CombatHandler.HuntingRange"/> of the bot, so stopping for anything further away would
    /// hand control to the combat handler for a monster it never fights - the bot would freeze in place
    /// (stop travelling, but find nothing to attack) until the monster happened to wander off.
    /// </summary>
    private const int TravelStopRange = HuntingRange;

    /// <summary>Heuristic weight while traveling: high enough to make the A* search greedy and cheap over long routes.</summary>
    private const int TravelHeuristicWeight = 12;

    /// <summary>
    /// Follow trigger for mini-game events: a participant further than this (tiles) from its
    /// leader walks back. Open-world party formation uses <see cref="PartyRegroupDistance"/> instead.
    /// </summary>
    private const int FollowDistance = 10;

    /// <summary>
    /// Open-world formation radius: with nothing to fight around the leader, a follower closes back
    /// in to within this distance (tiles). Deliberately tighter than <see cref="HuntingRange"/>, so the
    /// party stacks up when idle; the regroup walk itself is gated on "no fightable monster near the
    /// leader" (see <see cref="EvaluateFollowerHuntingAsync"/>), so combat drift out to hunting range
    /// never triggers a yank-back mid-fight.
    /// </summary>
    private const int PartyRegroupDistance = 2;

    /// <summary>
    /// Distance (tiles) from its leader beyond which a follower walks back even while there are monsters
    /// near the leader. Twice the hunting range: the combat handler only fights targets within
    /// <see cref="HuntingRange"/> of the leader, so no fight keeps a follower further away than that.
    /// </summary>
    private const int FollowerLeashDistance = 2 * HuntingRange;

    /// <summary>The game's own warp action, so a bot travels on exactly a player's terms - fare included.</summary>
    private static readonly WarpAction WarpAction = new();

    /// <summary>The game's own action to open a personal store, with all its checks of the prices.</summary>
    private static readonly OpenStoreAction MarketStoreOpenAction = new();

    /// <summary>
    /// How long a bot which found no better ground than the trivial one under its feet waits before it
    /// reviews that verdict. It farms there in the meantime - which is what earns it the gear, levels and
    /// Zen to reach better ground - instead of re-scanning every map of the world on every tick.
    /// </summary>
    private static readonly TimeSpan EscapeReviewInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often the bot takes its navigation decisions (where to go, whether to warp, shop, follow).
    /// Same pace as the MU Helper tick: a bot standing next to its leader sets off at most half a second
    /// after him. The errands checked on each decision keep their own, much longer intervals.
    /// </summary>
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How often the navigator timer fires. Most shots end right away; they only exist to notice that a
    /// travel hop has ended, so the next one starts without waiting for <see cref="EvaluationInterval"/>
    /// (see <see cref="TryBeginEvaluation"/>).
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan EmptyGroundGrace = TimeSpan.FromSeconds(8);

    /// <summary>How often the bot checks its backpack for equippable upgrades.</summary>
    private static readonly TimeSpan EquipCheckInterval = TimeSpan.FromSeconds(8);

    /// <summary>How often the bot considers whether it needs a shopping trip.</summary>
    private static readonly TimeSpan ShoppingCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Cooldown after a shopping trip before considering the next one.</summary>
    private static readonly TimeSpan ShoppingCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the bot re-evaluates whether it should claim a buff NPC's free buff: a level-eligible
    /// bot could claim it right away, so the check re-fires quickly while the buff has not yet been applied.
    /// </summary>
    private static readonly TimeSpan BuffCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cooldown after a buff trip before considering the next one, so bots which just got the buff
    /// (and level-eligible ones which could not claim it) don't re-walk to the NPC every check interval.
    /// </summary>
    private static readonly TimeSpan BuffCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the bot considers a visit to the chaos machine (see <see cref="BotChaosMachineHandler"/>).
    /// Short, so a bot standing at the goblin mixes its next piece right away; planning is cheap.
    /// </summary>
    private static readonly TimeSpan ChaosMachineCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Cooldown after a chaos machine trip which came to nothing (no route, no warp there, or a refused mix).</summary>
    private static readonly TimeSpan ChaosMachineCooldown = TimeSpan.FromMinutes(10);

    /// <summary>Minimum time between two cross-map warps, so a bot does not bounce between maps (a real player does not hop maps every minute either).</summary>
    private static readonly TimeSpan WarpCooldown = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a bot may go without landing a single hit before it treats its map as barren and steps
    /// down (see <see cref="TryPickEasierMap"/>). Long enough that a walk across a map, a merchant trip
    /// or a quiet stretch between spawns does not count as one.
    /// </summary>
    private static readonly TimeSpan BarrenMapDuration = TimeSpan.FromMinutes(3);

    /// <summary>Cooldown for following the party leader to another map (shorter, so the group regroups quickly).</summary>
    private static readonly TimeSpan FollowWarpCooldown = TimeSpan.FromSeconds(20);

    /// <summary>Cooldown between expensive same-map gate searches after walking to the leader failed.</summary>
    private static readonly TimeSpan FollowWarpSearchCooldown = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the party leader has to stay on a map before its bots follow him there. A leader who is
    /// only passing through (a town trip for supplies, a gate on the way somewhere) must not drag his
    /// whole party across the world and back - real party members wait to see where he settles, too.
    /// </summary>
    private static readonly TimeSpan LeaderSettleTime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Bounds of the random delay between becoming eligible for a character reset and performing it.
    /// A real player finishes the fight, walks to town, maybe trades first - and 250 bots resetting
    /// the very second they hit the required level would be an obvious give-away (and a thundering
    /// herd of simultaneous re-gearing). The delay is rolled per bot when it becomes eligible.
    /// </summary>
    private static readonly TimeSpan MinResetDelay = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound of the random reset delay, see <see cref="MinResetDelay"/>.</summary>
    private static readonly TimeSpan MaxResetDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// If the bot's position has not changed for this long, it is considered stuck (a wedged walk, or a
    /// monster it can't reach). The navigator then forces a fresh destination, so the bot gets going again.
    /// </summary>
    private static readonly TimeSpan StuckTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Stuck timeout when huntable monsters are nearby: much longer, because a bot standing still while
    /// monsters are around is usually just fighting them from one tile. It still eventually fires, which
    /// breaks the rare deadlock of a monster that is in range but cannot be attacked or reached.
    /// </summary>
    private static readonly TimeSpan StuckWithMonstersTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A pool of full-map pathfinders for long-distance travel. The pooled (scoped) pathfinder rejects
    /// start/end further apart than ~16 tiles, so travel uses a <see cref="FullGridNetwork"/> which resolves
    /// routes across the whole map. A pathfinder is not safe for concurrent use, so we keep a small pool and
    /// hand one out per call: several bots can plan routes in parallel (a single shared instance serialized
    /// every bot and starved navigation once the population reached the hundreds), while the pool size caps
    /// how many CPU cores whole-map searches may occupy at once.
    /// </summary>
    private static readonly int TravelPathFinderPoolSize = Math.Clamp(Environment.ProcessorCount / 3, 2, 6);

    private static readonly SemaphoreSlim TravelPathFinderPool = new(TravelPathFinderPoolSize, TravelPathFinderPoolSize);

    private static readonly ConcurrentBag<PathFinder> TravelPathFinders = CreateTravelPathFinderPool();

    private readonly OfflinePlayer _player;
    private readonly TimeProvider _timeProvider;
    private readonly BotDangerZoneTracker _dangerZones;
    private readonly BotStateMachine _states;

    private readonly CancellationTokenSource _cts = new();

    private Timer? _timer;
    private DateTime? _emptyGroundSince;
    private int _isEvaluating;
    private Point _destination;
    private bool _hasDestination;

    /// <summary>
    /// Whether the bot just ended a market shift and still stands on the market, which has no hunting
    /// ground: it warps home first, like after a shopping trip on a map without a merchant.
    /// </summary>
    private bool _leavesMarket;
    private DateTimeOffset _lastWarpUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFollowWarpSearchUtc = DateTimeOffset.MinValue;
    private Point _lastPosition;
    private DateTime _lastMoveUtc = DateTime.MinValue;

    /// <summary>
    /// The last "nothing better is reachable" verdict of the map choice, with the state it was made for
    /// (see <see cref="IsEscapeReviewDeferred"/>).
    /// </summary>
    private (int BotLevel, Guid MapId, DateTimeOffset RecheckAtUtc) _escapeReview;
    private DateTime _nextEquipCheckUtc = DateTime.MinValue;
    private IList<PathResultNode>? _travelPath;
    private int _travelPathIndex;
    private Point _travelPathTarget;
    private Point? _shoppingTarget;

    /// <summary>Target position of the current buff trip, or null, if there is none.</summary>
    private Point? _buffTarget;

    /// <summary>Target position of the current chaos machine trip, or null, if there is none.</summary>
    private Point? _chaosMachineTarget;

    /// <summary>The walkable tile next to the goblin the current chaos machine trip walks to.</summary>
    private Point _chaosMachineStandingPoint;

    /// <summary>
    /// When the bot considers a chaos machine trip again; the first one is spread over the cooldown, so
    /// a population does not crowd the goblin at once after a restart.
    /// </summary>
    private DateTime _nextChaosMachineCheckUtc = DateTime.UtcNow + (ChaosMachineCooldown * Rand.NextDouble());

    /// <summary>
    /// When the bot considers a buff trip again. Like the shopping check, the first one is spread over the
    /// cooldown (not "now"), so bots don't all march to the Elf Soldier at once.
    /// </summary>
    private DateTime _nextBuffCheckUtc = DateTime.UtcNow + (BuffCooldown * Rand.NextDouble());

    /// <summary>
    /// When this bot looks at its supplies again. Bots come up with the server, so starting them all at
    /// "now" sent the whole population to the same merchant in the same minute - and since the cooldown
    /// was the same for everyone, nothing ever broke that herd apart again. The first check is therefore
    /// spread over the cooldown, and every following one is spread around it.
    /// </summary>
    private DateTime _nextShoppingCheckUtc = DateTime.UtcNow + (ShoppingCooldown * Rand.NextDouble());
    private DateTime? _resetDueAtUtc;
    private GameConfiguration? _warpFloorConfiguration;
    private CharacterClass? _warpFloorClass;
    private int _minWarpLevel = FallbackMinWarpLevel;
    private Guid _leaderMapId;
    private DateTimeOffset _leaderOnMapSinceUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEvaluationUtc = DateTimeOffset.MinValue;
    private bool _isHopInProgress;

    /// <summary>
    /// The meeting for which the bot warped through the meeting's gate (see <see cref="BotEventMeeting.Gate"/>),
    /// and how often - so a bot which can't get any closer gives up instead of warping on and on.
    /// </summary>
    private BotEventMeeting? _gateWarpMeeting;

    private int _gateWarpCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotNavigator"/> class.
    /// </summary>
    /// <param name="player">The bot player.</param>
    public BotNavigator(OfflinePlayer player)
        : this(player, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BotNavigator"/> class.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="timeProvider">The time provider.</param>
    internal BotNavigator(OfflinePlayer player, TimeProvider timeProvider)
    {
        this._player = player;
        this._timeProvider = timeProvider;
        this._dangerZones = new BotDangerZoneTracker(player, timeProvider, this.GetHuntingZones);
        this._states = new BotStateMachine(timeProvider, this.IsErrandClaimed);
        this._player.Died += this.OnDied;
    }

    /// <summary>
    /// Gets the state machine which records what the bot does in each navigation tick.
    /// </summary>
    internal BotStateMachine States => this._states;

    /// <summary>
    /// Starts the periodic navigation evaluation. The start delay is jittered per bot, so hundreds of
    /// navigators do not all fire on the same second boundary (smoother load, less robotic synchrony).
    /// </summary>
    public void Start()
    {
        // The token is captured ONCE, not read from the (later disposed) source on every shot: a timer
        // callback which is already running when Dispose gets rid of the source would throw an
        // ObjectDisposedException right in the callback - unhandled, on a thread pool thread, taking the
        // whole server process down with it. Same pattern as the MU Helper tick.
        var cancellationToken = this._cts.Token;
        this._timer ??= new Timer(
            state => _ = this.SafeEvaluateAsync(cancellationToken),
            null,
            TimeSpan.FromMilliseconds(2000 + Rand.NextInt(0, 1500)),
            PollInterval);
    }

    /// <summary>
    /// Follows the party leader across maps after the leader has settled at the destination.
    /// </summary>
    /// <param name="map">The bot's current map.</param>
    /// <param name="leader">The party leader.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True, if evaluation should stop because a follow action was taken or the leader is on
    /// another map; false, if the bot can continue its normal local behavior.</returns>
    internal async ValueTask<bool> TryHandleCrossMapLeaderFollowAsync(GameMap map, Player leader, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(leader.CurrentMap, map))
        {
            return false;
        }

        ExitGate? warpListGate = null;
        if (leader.CurrentMap is { } targetMap
            && ((this.TryGetLegalWarp(targetMap.Definition, out var leaderWarp)
                    ? (warpListGate = leaderWarp.Gate) is null
                    : targetMap.Definition.Number != this._player.SelectedCharacter?.CharacterClass?.HomeMap?.Number)
                || targetMap.Definition.TryGetRequirementError(this._player, out _)))
        {
            // The leader moved to a map the bot cannot legally enter: its plain character level is
            // too low (level gates map access, the same rule as everywhere else), or it misses one of
            // the map's own requirements - like wings for Icarus, which the map choice checks too.
            // Rather than trail behind unreachable or sneak in through a back door, the bot leaves the group.
            if (this._player.Party is { } party)
            {
                this._player.Logger.LogDebug(
                    "Bot {Character} leaves its party: it cannot legally follow '{Leader}' to map {Map}.",
                    this._player.Name,
                    leader.Name,
                    targetMap.Definition.Name);
                await party.KickMySelfAsync(this._player).ConfigureAwait(false);
            }

            return true;
        }

        // Only follow a leader who SETTLED on the new map: a leader in transit (pulling a town
        // scroll for shopping, passing through a gate, hopping back and forth) used to drag its
        // whole party along on every hop - and since the followers land on the map's spawn gate
        // rather than next to him, they were still walking over when he warped away again.
        if (!this.HasLeaderSettled(leader))
        {
            return true;
        }

        // The follower warps like a player: through a warp entry it pays for (see
        // IsSavingForFollowFare for one which can't). It prefers the affordable entry closest to the
        // leader - landing on the map's spawn gate instead left it on the wrong floor of a multi-floor
        // map (Lost Tower, Dungeon), where it had to wait out the warp cooldown for a second warp to
        // the leader's floor. When no entry can reach the leader, the cheapest one will do. Only on
        // its class home map without any legal entry, it walks home through the spawn gate for free -
        // like the home retreat of the map choice. Without a fallback a follower could neither warp
        // after its leader nor hunt (following consumed its ticks), and only the stuck watchdog kept
        // it twitching between hunting grounds.
        if (this._timeProvider.GetUtcNow() - this._lastWarpUtc >= FollowWarpCooldown
            && leader.CurrentMap is { } leaderMap)
        {
            var fare = this.FindBestReachableLegalWarp(leaderMap, leader.Position, cancellationToken)
                       ?? this.GetLegalWarps(leaderMap.Definition)
                           .Where(w => w.Gate is not null && this.CanAffordWarp(w))
                           .MinBy(w => w.Costs);
            var homeGate = fare is null && warpListGate is null
                ? leaderMap.Definition.ExitGates.Where(g => g.IsSpawnGate).SelectRandom()
                : null;
            if (fare is null && homeGate is null)
            {
                return true;
            }

            this._lastWarpUtc = this._timeProvider.GetUtcNow();
            this._nextFollowWarpSearchUtc = DateTimeOffset.MinValue;
            this._hasDestination = false;
            this._travelPath = null;
            this._player.Logger.LogDebug(
                "Bot {Character} following party leader {Leader} to map {Map} through {Warp} (fare {Fare} zen).",
                this._player.Name,
                leader.Name,
                leaderMap.Definition.Name,
                fare?.ToString() ?? "its spawn gate",
                fare?.Costs ?? 0);
            if (fare is not null)
            {
                await WarpAction.WarpToAsync(this._player, fare).ConfigureAwait(false);
            }
            else
            {
                await this._player.WarpToAsync(homeGate!).ConfigureAwait(false);
            }

            // Where the bot ACTUALLY ended up: a gate landing on a blocked tile recovers it to the
            // safezone map, which isn't always the leader's (see TryWarpToBetterMapAsync).
            await this.TryPersistCurrentMapAsync(this._player.CurrentMap?.Definition ?? leaderMap.Definition).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Regroups with a leader on the same map by walking, or by using a reachable legal warp when
    /// disconnected floors make walking impossible.
    /// </summary>
    /// <param name="map">The shared map of the bot and its leader.</param>
    /// <param name="leader">The party leader to regroup with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if a walk or warp was performed; otherwise false.</returns>
    /// <remarks>
    /// The result is observed by focused regression tests. Production follower hunting returns after
    /// the regroup attempt regardless of whether an action was available.
    /// </remarks>
    internal async ValueTask<bool> TryRegroupWithLeaderOnSameMapAsync(GameMap map, Player leader, CancellationToken cancellationToken)
    {
        // Discard any previous hunting destination before resolving the moving leader again. Only
        // publish the leader as a destination after a route was found and a walk was actually issued.
        this._hasDestination = false;
        if (await this.TravelTowardAsync(map, leader.Position, cancellationToken).ConfigureAwait(false))
        {
            this._nextFollowWarpSearchUtc = DateTimeOffset.MinValue;
            this._destination = leader.Position;
            this._hasDestination = true;
            return true;
        }

        var now = this._timeProvider.GetUtcNow();
        if (now - this._lastWarpUtc < FollowWarpCooldown
            || now < this._nextFollowWarpSearchUtc)
        {
            return false;
        }

        // Some maps represent several floors as disconnected regions on the same map id
        // (Dungeon, Lost Tower, ...). If walking cannot reach the leader, regroup through the
        // closest legal warp entry whose complete landing area can reach him. Requiring every possible
        // landing point is deliberate: the warp performs one unvalidated random placement, so accepting
        // a partially usable gate would make following succeed or fail by chance.
        var leaderWarp = this.FindBestReachableLegalWarp(map, leader.Position, cancellationToken);
        if (leaderWarp?.Gate is not { } leaderGate)
        {
            // Start the full retry delay after a completed, unsuccessful scan. Keep this separate
            // from _lastWarpUtc, which only records real warps and controls other warp decisions.
            this._nextFollowWarpSearchUtc = this._timeProvider.GetUtcNow() + FollowWarpSearchCooldown;
            return false;
        }

        this._lastWarpUtc = this._timeProvider.GetUtcNow();
        this._travelPath = null;
        this._player.Logger.LogDebug(
            "Bot {Character} following party leader {Leader} within map {Map} through warp {Warp} (fare {Fare} zen).",
            this._player.Name,
            leader.Name,
            map.Definition.Name,
            leaderWarp.Name,
            leaderWarp.Costs);

        // Paid like a player's warp, which also checks every requirement the game checks.
        await WarpAction.WarpToAsync(this._player, leaderWarp).ConfigureAwait(false);
        await this.TryPersistCurrentMapAsync(this._player.CurrentMap?.Definition ?? map.Definition).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Whether the leader has been on his current map long enough (<see cref="LeaderSettleTime"/>) for the
    /// bot to follow him there. Tracks the leader's map instance per bot, so each follower makes the call on its own.
    /// </summary>
    /// <param name="leader">The party leader.</param>
    /// <returns>True, if the leader has settled long enough.</returns>
    internal bool HasLeaderSettled(Player leader)
    {
        if (leader.CurrentMap is not { } leaderMap)
        {
            return false;
        }

        if (this._leaderMapId != leaderMap.Id)
        {
            this._leaderMapId = leaderMap.Id;
            this._leaderOnMapSinceUtc = this._timeProvider.GetUtcNow();
        }

        return this._timeProvider.GetUtcNow() - this._leaderOnMapSinceUtc >= LeaderSettleTime;
    }

    /// <summary>
    /// Whether the bot needs a warp to reach its leader - he is on another map, or on a part of the
    /// same map it can't walk to (see <see cref="BotMapRegions"/>) - but can't afford any warp entry
    /// which it may legally take there.
    /// </summary>
    /// <param name="map">The bot's current map.</param>
    /// <param name="leader">The party leader.</param>
    /// <returns>True, if the bot has to save up for the fare.</returns>
    /// <remarks>
    /// A map the bot may not enter at all is not a matter of saving: the follow logic makes the bot
    /// leave the party then (see <see cref="TryHandleCrossMapLeaderFollowAsync"/>).
    /// </remarks>
    internal bool IsSavingForFollowFare(GameMap map, Player leader)
    {
        if (leader.CurrentMap is not { } leaderMap
            || leaderMap.Definition.TryGetRequirementError(this._player, out _))
        {
            return false;
        }

        if (ReferenceEquals(leaderMap, map))
        {
            var regions = BotMapRegions.Get(map.Definition);
            var ownRegion = regions.GetRegion(this._player.Position);
            var leaderRegion = regions.GetRegion(leader.Position);
            if (ownRegion == 0 || leaderRegion == 0 || ownRegion == leaderRegion)
            {
                return false;
            }
        }

        var entries = this.GetLegalWarps(leaderMap.Definition).Where(w => w.Gate is not null).ToList();
        return entries.Count > 0 && !entries.Any(this.CanAffordWarp);
    }

    /// <summary>
    /// Picks a hunting ground and starts walking to it. If the chosen ground turns out to be unreachable, the
    /// destination and its claim are dropped, so the next call (next tick) simply picks another one.
    /// </summary>
    /// <param name="map">The map to pick a hunting ground on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal async ValueTask PickGroundAndTravelAsync(GameMap map, CancellationToken cancellationToken)
    {
        if (!this.TryPickHuntingGround(map, out var ground, out var groundLevel))
        {
            this._hasDestination = false;
            this._player.HuntingSpot = null;
            this._player.Logger.LogDebug("Bot {Character}: no hunting ground found on map {Map}.", this._player.Name, map.Definition.Name);
            return;
        }

        this._player.HuntingSpot = new HuntingSpot(ground, map.Definition.Number);
        this._destination = ground;
        this._hasDestination = true;
        this._player.Logger.LogDebug(
            "Bot {Character} (level {Level}) heading to hunting ground {Ground} (monster level ~{MonsterLevel}).",
            this._player.Name,
            this.GetBotLevel(),
            ground,
            groundLevel);

        if (!await this.TravelTowardAsync(map, ground, cancellationToken).ConfigureAwait(false))
        {
            this._hasDestination = false;
            this._player.HuntingSpot = null;
        }
    }

    /// <summary>
    /// Finds the position of the nearest live, attackable monster within <see cref="MonsterSeekRadius"/> that
    /// the bot can safely fight, so the bot heads straight for a real monster instead of a random tile in a
    /// huge spawn area. Returns false when none are loaded nearby (or all are above the safe level cap), in
    /// which case the caller falls back to coarse spawn-area travel / warping to relocate.
    /// </summary>
    /// <param name="map">The map to search on.</param>
    /// <param name="ground">The position of the chosen monster.</param>
    /// <returns>True, if a safe monster was found.</returns>
    internal bool TryFindNearestMonsterGround(GameMap map, out Point ground)
    {
        ground = default;
        var position = this._player.Position;
        var avoidedZones = this.GetAvoidedZones(map.Definition);
        var attackables = map.GetAttackablesInRange(position, MonsterSeekRadius);
        var candidates = new List<(Monster Monster, double Distance)>();
        foreach (var attackable in attackables)
        {
            if (attackable is not Monster { SummonedBy: null } monster
                || !monster.IsAlive
                || monster.IsAtSafezone())
            {
                continue;
            }

            if (avoidedZones is { } avoided && avoided.Avoided.Contains(avoided.Zones.GetZone(monster.Position)))
            {
                // The bot keeps away from that zone - homing in on its monsters would lead it right back.
                continue;
            }

            if (!CombatHandler.IsSafeTarget(this._player, monster.Definition))
            {
                // Too dangerous (judged by its real damage, not its level) - leave it and let the warp /
                // area logic relocate the bot to a map whose monsters it can handle.
                continue;
            }

            candidates.Add((monster, monster.GetDistanceTo(position)));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // Prefer monsters no other bot outside the party is already hunting. If every nearby monster is
        // contested, the ground is saturated - sharing it beats standing around, so the bot still picks one.
        var competitorSpots = HuntingSpotOccupancy.GetCompetitorSpots(this._player, attackables.OfType<Player>(), map.Definition.Number);
        var unclaimed = candidates
            .Where(c => !HuntingSpotOccupancy.IsMonsterClaimed(c.Monster.Position, competitorSpots, HuntingRange))
            .ToList();
        if (unclaimed.Count == 0)
        {
            unclaimed = candidates;
        }

        // Head for one of the few nearest monsters, chosen randomly, instead of strictly the nearest:
        // with many bots on one ground, deterministic nearest-first would send them all to the same
        // monster, and they would roam the map as one pack - a very bot-like look.
        var chosen = unclaimed
            .OrderBy(c => c.Distance)
            .Take(3)
            .Select(c => c.Monster)
            .SelectRandom();
        if (chosen is null)
        {
            return false;
        }

        // The monster stands on a walkable tile, so head straight for it; TravelTowardAsync walks a few steps
        // per tick and re-homes as the monster roams, closing the gap until combat range is reached. Claiming
        // the target keeps other bots from converging on the same monster while this one is still on its way.
        this._player.HuntingSpot = new HuntingSpot(chosen.Position, map.Definition.Number);
        ground = chosen.Position;
        return true;
    }

    /// <summary>
    /// Drives an event meeting (see <see cref="OfflinePlayer.EventMeeting"/>): a bot which announced an
    /// opened event warps to the meeting map, walks up to the event's entry NPC (or just into the
    /// safezone, for Chaos Castle) and waits there until the entrance closes. The meeting ends early
    /// when a player takes the bot into a party - the follow logic then takes it into the event behind
    /// its new leader. A meeting place which a player named comes with the gate to enter it through
    /// (see <see cref="BotEventMeeting.Gate"/>), so the bot gets there like a player would - by the
    /// warp closest to the place, not by a walk from the town.
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="inSafezone">Indicates whether the bot currently is in the safezone of the map.</param>
    /// <param name="cancellationToken">The token which aborts the travel wait on shutdown.</param>
    /// <returns>True, if the meeting consumed this tick.</returns>
    internal async ValueTask<bool> TryEventMeetingAsync(GameMap map, bool inSafezone, CancellationToken cancellationToken)
    {
        if (this._player.EventMeeting is not { } meeting)
        {
            return false;
        }

        if (DateTime.UtcNow >= meeting.UntilUtc
            || this._player.CurrentMiniGame is not null
            || BotPartyHandler.HasHumanCompanion(this._player))
        {
            this.EndEventMeeting(meeting);
            return false;
        }

        // Leaving the hunting ground for a while: don't keep other bots off it meanwhile.
        this._player.HuntingSpot = null;

        if (map.Definition.Number != meeting.Map.Number || (meeting.NpcPosition is null && !inSafezone))
        {
            if ((meeting.Gate ?? meeting.Map.ExitGates.Where(g => g.IsSpawnGate).SelectRandom()) is not { } gate)
            {
                this.EndEventMeeting(meeting);
                return false;
            }

            await this.WarpToMeetingAsync(meeting, gate).ConfigureAwait(false);
            return true;
        }

        if (meeting.NpcPosition is { } npcPosition && this._player.GetDistanceTo(npcPosition) > BotTownTripPlanner.NpcInteractionRange)
        {
            var standingPoint = BotEventMeetingPlanner.FindStandingPoint(map.Terrain, npcPosition, BotTownTripPlanner.NpcInteractionRange);

            // Already on the map, but the meeting's gate lands closer - another floor of the Lost Tower,
            // or Devias3 while the bot stands in the town of Devias: warp instead of a long walk, or none
            // at all. The second try is for a landing on a blocked tile, which ends at the spawn gate.
            if (standingPoint is { } point
                && meeting.Gate is { } meetingGate
                && this.GetGateWarpCount(meeting) < 2
                && !BotWarpPlanner.IsInside(meetingGate, this._player.Position)
                && BotWarpPlanner.GateDistance(meetingGate, point) < this._player.GetDistanceTo(point))
            {
                await this.WarpToMeetingAsync(meeting, meetingGate).ConfigureAwait(false);
                return true;
            }

            if (standingPoint is null
                || !await this.TravelTowardAsync(map, standingPoint.Value, cancellationToken).ConfigureAwait(false))
            {
                this._player.Logger.LogDebug("Bot '{Name}' gives up waiting for the event: no route to the entry NPC at {Target}.", this._player.Name, npcPosition);
                this.EndEventMeeting(meeting);
                return false;
            }

            return true;
        }

        // Waiting at the meeting point is not "stuck".
        this._lastMoveUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Determines whether the navigator evaluates on this timer shot, and if so, starts the next interval:
    /// once per <see cref="EvaluationInterval"/>, and additionally as soon as a travel hop has ended.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c>, if the bot should take its next navigation decision now.</returns>
    /// <remarks>
    /// A hop of <see cref="StepsPerHop"/> steps takes about a second. When the hop still ran at a decision,
    /// that decision was skipped, and the bot stood until the next one - it walked in bursts, at about half
    /// the speed of a player. Continuing right after the hop keeps it moving, while it still stops between
    /// hops when a monster comes into range.
    /// </remarks>
    internal bool TryBeginEvaluation(DateTimeOffset now)
    {
        if (now < this._nextEvaluationUtc && !(this._isHopInProgress && !this._player.IsWalking))
        {
            return false;
        }

        this._nextEvaluationUtc = now + EvaluationInterval;
        this._isHopInProgress = false;
        return true;
    }

    /// <summary>
    /// Gets the legal warp entries to a map which the bot could hunt through, each with the level of the
    /// strongest safe monster in its zone (see <see cref="BotMapZones"/> and <see cref="BestSafeLevel"/>).
    /// </summary>
    /// <param name="mapDefinition">The map.</param>
    /// <param name="minimumLevel">The lowest monster level worth counting.</param>
    /// <returns>The warp entries with their monster level.</returns>
    /// <remarks>
    /// Each entry only counts the monsters closer to it on foot than to any other entry: the floors of
    /// the Lost Tower can't be walked between at all, and the zones of Elvenland or Atlans lie up to a
    /// few hundred steps apart. Judging the whole map by its strongest monsters and entering through the
    /// first entry sent the bots to a floor which held none of them, or on a long walk. The bot's own
    /// map counts too, through the entries to the other zones - a bot on the first floor of the Lost
    /// Tower may warp up to a better one. Zones the bot died in too often are left out
    /// (see <see cref="BotDangerZoneTracker"/>).
    /// </remarks>
    internal IEnumerable<(WarpInfo Warp, int Level)> GetHuntingWarps(GameMapDefinition mapDefinition, int minimumLevel)
    {
        this._dangerZones.Update();
        if (this.GetHuntingZones(mapDefinition) is not { } hunting)
        {
            yield break;
        }

        var currentZone = ReferenceEquals(mapDefinition, this._player.CurrentMap?.Definition)
            ? hunting.Zones.GetZone(this._player.Position)
            : -1;
        for (var entry = 0; entry < hunting.Entries.Count; entry++)
        {
            if (!hunting.Zones.HasZone(entry)
                || entry == currentZone
                || this._dangerZones.IsAvoided(hunting.Entries[entry]))
            {
                continue;
            }

            var zone = entry;
            yield return (hunting.Entries[entry], this.BestSafeLevel(mapDefinition, minimumLevel, area => hunting.Zones.IsAreaWithin(area, zone, BotHuntingGroundSelector.GroundScatterSpan, HuntingRange)));
        }
    }

    /// <summary>
    /// Notes a death by a monster, so the bot keeps away from that place when it dies there too often
    /// (see <see cref="BotDangerZoneTracker"/>).
    /// </summary>
    /// <param name="map">The map of the death.</param>
    /// <param name="position">The position of the death.</param>
    internal void RegisterMonsterDeath(GameMapDefinition map, Point position) => this._dangerZones.RegisterDeath(map, position);

    /// <summary>
    /// Gets the maps worth moving to, each through the entry to its best part, along with the level of the
    /// strongest monster the bot can safely fight there.
    /// </summary>
    /// <param name="escape">Whether the bot has to leave its current map regardless of its strength.</param>
    /// <param name="minimumMonsterLevel">The lowest monster level worth counting at all.</param>
    /// <param name="escapeFloor">The lowest monster level an escape target has to offer.</param>
    /// <returns>The candidate maps, unordered.</returns>
    /// <remarks>
    /// Public to the assembly so the map choice can be checked by the bot map tests without running a world.
    /// </remarks>
    internal List<BotMapChoicePlanner.MapChoice> GetBetterMapCandidates(bool escape, int minimumMonsterLevel, int escapeFloor)
    {
        var current = this._player.CurrentMap?.Definition;
        var currentGroundLevel = current is null ? 0 : this.BestSafeLevel(current, minimumMonsterLevel, this.GetCurrentZoneFilter());
        var targetLevel = BotMapChoicePlanner.TargetLevel(escape, escapeFloor, currentGroundLevel);
        var judgedFrom = BotMapChoicePlanner.JudgeFrom(minimumMonsterLevel, targetLevel);
        var candidates = new List<BotMapChoicePlanner.MapChoice>();
        foreach (var candidate in this._player.GameContext.Configuration.Maps)
        {
            // The whole map is an upper bound for every part of it, and cheap to judge.
            if (this.BestSafeLevel(candidate, judgedFrom) < targetLevel
                || candidate.TryGetRequirementError(this._player, out _)
                || !BotWarpPlanner.HasWalkableSpawnGate(candidate))
            {
                continue;
            }

            // One candidate per map, through the entry to its best part: the ranking in
            // BotMapChoicePlanner.PickStrongest spreads the bots over maps, and several floors of one map
            // must not crowd the others out of it.
            BotMapChoicePlanner.MapChoice? bestEntry = null;
            foreach (var (warp, level) in this.GetHuntingWarps(candidate, judgedFrom))
            {
                if (level > (bestEntry?.Level ?? 0)
                    && BotMapChoicePlanner.IsWorthEntering(level, targetLevel, this.CanAffordWarp(warp), BotWarpPlanner.HasWalkableLanding(warp)))
                {
                    bestEntry = new BotMapChoicePlanner.MapChoice(warp, candidate, level);
                }
            }

            if (bestEntry is { } chosen)
            {
                candidates.Add(chosen);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Whether the map choice of a bot on trivial ground is deferred because its last review found nothing
    /// better within reach (see <see cref="DeferEscapeReview"/>). A level-up lifts the deferral at once -
    /// the verdict was made for the old level.
    /// </summary>
    /// <param name="botLevel">The bot's current effective level.</param>
    /// <param name="currentMap">The bot's current map.</param>
    /// <returns><c>true</c>, if the review should wait.</returns>
    /// <remarks>Public to the assembly so the bot map tests can check the deferral by itself.</remarks>
    internal bool IsEscapeReviewDeferred(int botLevel, GameMapDefinition? currentMap)
    {
        var (reviewedLevel, reviewedMapId, recheckAtUtc) = this._escapeReview;
        return this._timeProvider.GetUtcNow() < recheckAtUtc
               && reviewedLevel == botLevel
               && reviewedMapId == (currentMap?.GetId() ?? Guid.Empty);
    }

    /// <summary>
    /// Defers the next review of the map choice for <see cref="EscapeReviewInterval"/>, because the bot
    /// found no ground worth leaving for.
    /// </summary>
    /// <param name="botLevel">The bot's current effective level.</param>
    /// <param name="currentMap">The bot's current map.</param>
    /// <remarks>Public to the assembly so the bot map tests can check the deferral by itself.</remarks>
    internal void DeferEscapeReview(int botLevel, GameMapDefinition? currentMap)
        => this._escapeReview = (botLevel, currentMap?.GetId() ?? Guid.Empty, this._timeProvider.GetUtcNow() + EscapeReviewInterval);

    /// <summary>
    /// Gets the escape gate to the bot's class hometown, for when it must leave its current map but is
    /// below every warp requirement (e.g. a freshly reset veteran) - the equivalent of a town scroll.
    /// </summary>
    /// <param name="allowTrivialHome">
    /// Whether going home is acceptable even though the bot would only find trivial monsters there. That is
    /// the case for an errand which needs a town (shopping, the buff NPC, leaving the market), but not for
    /// escaping a bad hunting ground: warping a level-250 bot back to Lorencia is the very thing it needs
    /// to escape from.
    /// </param>
    /// <param name="gate">The home map safezone gate.</param>
    /// <param name="mapDefinition">The home map.</param>
    /// <param name="monsterLevel">The level of the strongest safe monster there (informational).</param>
    /// <returns>True, if the bot has a home map it is not already on, which is worth going to.</returns>
    /// <remarks>Public to the assembly so the bot map tests can check the rule without a trip.</remarks>
    internal bool TryGetHomeEscapeGate(bool allowTrivialHome, [MaybeNullWhen(false)] out ExitGate gate, [MaybeNullWhen(false)] out GameMapDefinition mapDefinition, out int monsterLevel)
    {
        gate = null;
        mapDefinition = null;
        monsterLevel = 0;

        var homeMap = this._player.SelectedCharacter?.CharacterClass?.HomeMap;
        if (BotTownTripPlanner.TryFindHomeGate(
                homeMap,
                this._player.CurrentMap?.Definition.Number,
                isForbidden: BotFeaturePlugIn.GetConfiguration(this._player.GameContext)?.IsMapForbidden(homeMap) == true,
                homeMonsterLevel: homeMap is null ? 0 : this.BestSafeLevel(homeMap),
                botLevel: this.GetBotLevel(),
                allowTrivialHome) is not { } home)
        {
            return false;
        }

        gate = home.Gate;
        mapDefinition = home.Map;
        monsterLevel = home.MonsterLevel;
        return true;
    }

    /// <summary>
    /// Hunts around the party leader on the same map: monsters within hunting range
    /// (<see cref="HuntingRange"/>, mirroring the follower's effective combat range of
    /// <see cref="Bots.BotMuHelperSettings.HuntingRange"/>) of the leader are fought via the combat
    /// handler (tethered through <see cref="OfflinePlayer.HuntingOrigin"/>, which is also set where the
    /// origin is decided so it holds on walking ticks), and only with nothing fightable near the leader
    /// does the bot close back in to within <see cref="PartyRegroupDistance"/>. Followers never pick
    /// independent hunting grounds - the leader decides where the party hunts.
    /// </summary>
    /// <remarks>
    /// Two cases close in regardless of monsters: a follower in the safezone, where it cannot fight, and
    /// a follower further than <see cref="FollowerLeashDistance"/> from its leader.
    /// </remarks>
    /// <param name="map">The map of the bot and its leader.</param>
    /// <param name="leader">The party leader.</param>
    /// <param name="inSafezone">Whether the bot stands in the safezone.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal async ValueTask EvaluateFollowerHuntingAsync(GameMap map, Player leader, bool inSafezone, CancellationToken cancellationToken)
    {
        // Tether combat to the leader, so the bot fights what is near him instead of wandering off.
        this._player.HuntingOrigin = leader.Position;
        var distanceToLeader = this._player.GetDistanceTo(leader.Position);

        if (inSafezone)
        {
            // Nothing to fight in town, so the bot keeps up with its leader - also when he heads out to the
            // monsters at the gate. It used to stand still here and only moved when the stuck watchdog
            // fired: three steps every few seconds.
            if (distanceToLeader > PartyRegroupDistance)
            {
                _ = await this.TryRegroupWithLeaderOnSameMapAsync(map, leader, cancellationToken).ConfigureAwait(false);
                return;
            }

            this._hasDestination = false;
            this._emptyGroundSince = null;
            return;
        }

        var monsterNearLeader = map.GetAttackablesInRange(leader.Position, HuntingRange)
            .OfType<Monster>()
            .Any(m => m.IsAlive && !m.IsAtSafezone() && m.SummonedBy is null && CombatHandler.IsSafeTarget(this._player, m.Definition));
        if (monsterNearLeader && distanceToLeader <= FollowerLeashDistance)
        {
            // Something fightable near the leader: hold position and let the combat handler engage,
            // even when drifted out to hunting range - walking back now would interrupt the fight.
            this._hasDestination = false;
            this._emptyGroundSince = null;
            return;
        }

        // Nothing to fight around the leader, or the bot fell so far behind that no fight explains it
        // (e.g. it holds for a monster it cannot reach while the leader runs on): close back into formation.
        if (distanceToLeader > PartyRegroupDistance)
        {
            // The result is used by direct regression tests. This production flow returns after the
            // regroup attempt either way, so there is no subsequent behavior to condition on it.
            _ = await this.TryRegroupWithLeaderOnSameMapAsync(map, leader, cancellationToken).ConfigureAwait(false);
            return;
        }

        this._hasDestination = false;
        this._emptyGroundSince = null;
    }

    /// <summary>
    /// Opens the personal store at the stall, as it runs inside the MU Helper tick.
    /// </summary>
    /// <param name="shift">The market shift.</param>
    /// <returns>The task.</returns>
    internal async ValueTask OpenMarketStoreAsync(BotMarketShift shift)
    {
        // The plugin may have been switched off since the opening was queued; the next tick ends the shift.
        if (!ReferenceEquals(this._player.MarketShift, shift)
            || shift.IsEnding
            || this._player.GameContext.FeaturePlugIns.GetPlugIn<BotMerchantPlugIn>() is null)
        {
            return;
        }

        await MarketStoreOpenAction.OpenStoreAsync(this._player, shift.StoreName).ConfigureAwait(false);
        if (this._player.ShopStorage?.StoreOpen ?? false)
        {
            // What the bot has now is its own - every zen and item which comes in later is income (see
            // BotMerchandise.RemoveIncomeAsync).
            shift.MoneyAtOpen = this._player.Money;
            shift.ItemsAtOpen = this._player.Inventory?.Items.ToHashSet() ?? [];
            shift.LastSaleUtc = DateTime.UtcNow;
            shift.IsStoreOpened = true;
            this._player.Logger.LogDebug("Bot '{Name}' opened its store '{Store}' at {Stall}.", this._player.Name, shift.StoreName, shift.Stall);
        }
        else
        {
            // E.g. a price in a currency which isn't allowed (anymore). It ends like any other shift, so the
            // bot waits for its next one instead of coming back right away to fail again.
            var configuration = this._player.GameContext.FeaturePlugIns.GetPlugIn<BotMerchantPlugIn>()?.Configuration;
            this.EndMarketShift(shift, configuration, BotMarketPlanner.EndReason.StoreNotOpened);
        }
    }

    /// <summary>
    /// Gets the gate of a neighbouring map which is a real improvement over the current one, for a bot
    /// which cannot afford any warp. Map gates cost nothing, so this is the road out of a starter map
    /// for a broke bot which would otherwise farm its level-1 monsters forever (issue #1542). The same
    /// access rules as for a player apply: the gate's level requirement, the target map's requirements
    /// and the bot's safety check against the target's monsters.
    /// </summary>
    /// <param name="currentMap">The map the bot stands on.</param>
    /// <param name="currentLevel">The strongest monster on the current map the bot can safely fight.</param>
    /// <param name="gate">The target gate of the chosen map gate.</param>
    /// <param name="mapDefinition">The chosen target map.</param>
    /// <param name="monsterLevel">The level of the strongest safe monster there.</param>
    /// <returns>True, if a neighbouring map worth taking was found.</returns>
    internal bool TryGetGateEscape(
        GameMapDefinition currentMap,
        int currentLevel,
        [MaybeNullWhen(false)] out ExitGate gate,
        [MaybeNullWhen(false)] out GameMapDefinition mapDefinition,
        out int monsterLevel)
    {
        gate = null;
        mapDefinition = null;
        monsterLevel = 0;

        if (this._player.SelectedCharacter is not { } character)
        {
            return false;
        }

        var plainLevel = (int)(this._player.Attributes?[Stats.Level] ?? 1);
        var minimumLevel = Math.Max(currentLevel + BotMapChoicePlanner.ImprovementMargin, 1);
        var configuration = BotFeaturePlugIn.GetConfiguration(this._player.GameContext);
        foreach (var enterGate in currentMap.EnterGates)
        {
            if (enterGate.TargetGate is not { } targetGate
                || targetGate.Map is not { } candidate
                || candidate.Number == currentMap.Number
                || !candidate.IsActive
                || configuration?.IsMapForbidden(candidate) == true
                || candidate.GetSafezoneGate() is null
                || !BotWarpPlanner.HasWalkableSpawnGate(candidate)
                || character.GetEffectiveMoveLevelRequirement(enterGate.LevelRequirement) > plainLevel
                || candidate.TryGetRequirementError(this._player, out _))
            {
                continue;
            }

            var level = this.BestSafeLevel(candidate);
            if (level < minimumLevel || level <= monsterLevel)
            {
                continue;
            }

            gate = targetGate;
            mapDefinition = candidate;
            monsterLevel = level;
        }

        return gate is not null;
    }

    /// <summary>
    /// Picks a hunting ground point from the map's monster spawn areas, preferring the strongest monsters
    /// the bot can safely fight (see <see cref="CombatHandler.IsSafeTarget"/>) - best experience without
    /// being slaughtered. If nothing on the map is safe, it falls back to the weakest monsters available
    /// (the bot should warp away anyway).
    /// </summary>
    /// <param name="map">The map the bot hunts on.</param>
    /// <param name="ground">The chosen ground point.</param>
    /// <param name="groundLevel">The monster level of the chosen ground.</param>
    /// <returns><c>True</c>, if a ground was picked; otherwise, <c>false</c>.</returns>
    internal bool TryPickHuntingGround(GameMap map, out Point ground, out int groundLevel)
    {
        ground = default;
        groundLevel = 0;

        // Only grounds in the part of the map the bot can walk to: on the Lost Tower, the strongest
        // monsters stand on a floor above, and picking them made every walk fail and the bot stand idle.
        // A bot which stands on a blocked tile - on the gate it landed on, or next to a wall - is judged by
        // the region next to it; without a walkable tile at all, no ground of the map can be walked to.
        var mapRegions = BotMapRegions.Get(map.Definition);
        var region = mapRegions.GetRegionNear(this._player.Position, RegionSearchRadius);
        if (region == 0)
        {
            return false;
        }

        int[] regions = [region];
        var isAvoided = this.GetAvoidedGroundFilter(map.Definition);
        var candidates = map.Definition.MonsterSpawns
            .Where(BotHuntingGroundSelector.IsPermanentMonsterSpawn)
            .Where(a => mapRegions.IsAreaWithin(a, regions, BotHuntingGroundSelector.GroundScatterSpan, HuntingRange))
            .Where(a => isAvoided is null || !isAvoided(a))
            .Select(a => (Area: a, Level: BotHuntingGroundSelector.GetMonsterLevel(a.MonsterDefinition!)))
            .Where(x => x.Level > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        // Prefer the strongest monsters the bot can safely handle (best experience); if none on this map
        // pass the safety check, fall back to the weakest available (the bot should warp away anyway).
        var safe = candidates.Where(x => CombatHandler.IsSafeTarget(this._player, x.Area.MonsterDefinition!)).ToList();

        var floor = this.MasterExperienceFloor();
        var band = BotHuntingGroundSelector.SelectLevelBand(candidates, safe, floor);

        // After repeated deaths against the same player, the area around the death site is off the
        // menu for a while (see OfflinePlayer.TryGetDeathSiteToAvoid): drop grounds close to it, so
        // the bot farms somewhere else instead of walking back into the same lost fight.
        Point? siteToAvoid = this._player.TryGetDeathSiteToAvoid(map.Definition, out var avoidPoint) ? avoidPoint : null;
        band = BotHuntingGroundSelector.WithoutDeathSite(band, siteToAvoid);

        // Grounds another bot outside the party already works are dropped first, so two independent
        // bots do not farm the same monsters.
        var competitorSpots = HuntingSpotOccupancy.GetCompetitorSpots(this._player, map.GetPlayers(), map.Definition.Number);
        band = BotHuntingGroundSelector.WithoutCompetitors(band, competitorSpots);

        if (BotHuntingGroundSelector.PickWeighted(band, this._player.Position) is not { } chosen)
        {
            return false;
        }

        groundLevel = chosen.Level;
        return BotHuntingGroundSelector.TryPickWalkablePoint(map, chosen.Area, siteToAvoid, competitorSpots, mapRegions, region, out ground);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (this._timer is { } timer)
        {
            this._timer = null;
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        await this._cts.CancelAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this._player.Died -= this.OnDied;
            this._timer?.Dispose();
            this._timer = null;
            this._cts.Dispose();
        }

        base.Dispose(disposing);
    }

    private static PathFinder CreateTravelPathFinder()
    {
        return new PathFinder(new FullGridNetwork(true))
        {
            Heuristic = new ManhattanTravelHeuristic(),
            HeuristicEstimate = TravelHeuristicWeight,
            SearchLimit = BotWarpPlanner.TravelSearchLimit,
        };
    }

    private static ConcurrentBag<PathFinder> CreateTravelPathFinderPool()
    {
        var pool = new ConcurrentBag<PathFinder>();
        for (var i = 0; i < TravelPathFinderPoolSize; i++)
        {
            pool.Add(CreateTravelPathFinder());
        }

        return pool;
    }

    private static DateTime NextShoppingCheckUtc()
    {
        var spread = 1 + (((Rand.NextDouble() * 2) - 1) * ShoppingSpread);
        return DateTime.UtcNow + (ShoppingCooldown * spread);
    }

    /// <summary>
    /// Returns the time of the next buff consideration, spread around <see cref="BuffCooldown"/> so that bots
    /// which just went to the buff NPC together don't line up there again.
    /// </summary>
    /// <returns>The time of the next buff consideration.</returns>
    private static DateTime NextBuffCheckUtc()
    {
        var spread = 1 + (((Rand.NextDouble() * 2) - 1) * BuffSpread);
        return DateTime.UtcNow + (BuffCooldown * spread);
    }

    private async Task SafeEvaluateAsync(CancellationToken cancellationToken)
    {
        // The timer fires on a fixed interval regardless of whether the previous evaluation finished;
        // skip overlapping runs so a single bot never has two travel walks / searches in flight.
        if (Interlocked.CompareExchange(ref this._isEvaluating, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!this.TryBeginEvaluation(this._timeProvider.GetUtcNow()))
            {
                return;
            }

            await this.EvaluateAsync(cancellationToken).ConfigureAwait(false);
            this._player.OnAiTickSucceeded();
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "Bot navigator error for {Account}.", this._player.AccountLoginName);
            this._player.OnAiTickFailed();
        }
        finally
        {
            this._isEvaluating = 0;
        }
    }

    /// <summary>
    /// Gathers what one evaluation tick works with, and runs the upkeep which has to happen on every tick:
    /// party bookkeeping, the potion refill and - every few minutes - the loot-driven equipment, skill and
    /// wing progression. The upkeep is queued into the MU Helper tick, never run from here: all of it
    /// mutates items, attributes or skills, which must not happen while the combat handler walks the same
    /// structures on its own timer (see <see cref="OfflinePlayer.PendingBotActions"/>).
    /// </summary>
    /// <param name="map">The map the bot stands on.</param>
    /// <returns>What the tick works with: the safezone, the leader, the tether and the monsters nearby.</returns>
    private async ValueTask<TickContext> PrepareTickAsync(GameMap map)
    {
        // Party bookkeeping: answer a pending invitation from a player once its human-like delay
        // passed (see BotPartyHandler).
        await BotPartyHandler.ProcessAsync(this._player).ConfigureAwait(false);

        // Make sure the bot always carries healing and mana potions. Without them, the offline handlers
        // have nothing to drink: the bot dies to sustained damage, and casters degrade to weak melee
        // once their mana runs dry. This also tops up already-generated bots at runtime.
        // Queued into the MuHelper tick: an ammunition refill lands in an equipped hand slot, which
        // mounts item power-ups into the attribute system - and any attribute mutation from this
        // (separate) timer racing the combat handler's recalculations can corrupt the attribute
        // graph permanently (see PendingBotActions).
        this._player.PendingBotActions.Enqueue(() => this.EnsurePotionsAsync());

        // Periodically evaluate looted gear and equip upgrades (drops the replaced piece), so the bot's
        // equipment progresses over its lifetime like a real player's. Queued for the same reason:
        // equipping mounts/unmounts item power-ups.
        if (DateTime.UtcNow >= this._nextEquipCheckUtc)
        {
            this._nextEquipCheckUtc = DateTime.UtcNow + EquipCheckInterval;
            this._player.PendingBotActions.Enqueue(() => BotEquipmentHandler.TryEquipUpgradesAsync(this._player));

            // Looted skill orbs and scrolls are consumed into new skills like a human would (see
            // BotSkillHandler). Queued for the same reason: learning mutates the skill list the combat
            // handler may be enumerating on its own timer.
            this._player.PendingBotActions.Enqueue(() => BotSkillHandler.TryLearnSkillsAsync(this._player));

            // Wings don't drop, so the loot-driven equipment progression above never provides them;
            // they are earned at the classic level milestones instead (see BotWingHandler). Queued
            // for the same reason: equipping mount item power-ups.
            this._player.PendingBotActions.Enqueue(() => BotWingHandler.TryAdvanceWingsAsync(this._player));
        }

        // Keep the combat centre on the bot's current position, so the combat handler always engages
        // monsters right next to the bot - both at the hunting ground and while travelling through hostile
        // territory (self-defence). This is what keeps a travelling bot alive. The travel destination is
        // tracked separately in _destination.
        var inSafezone = map.Terrain.SafezoneMap[this._player.Position.X, this._player.Position.Y];

        // Resolved here (not just before the follow branch) so the tether below holds on every tick,
        // including ticks that return early while walking or on an errand.
        var leaderToFollow = this.GetPartyLeaderToFollow();

        // A follower which can't pay the fare to its leader stays in the party and hunts where it
        // stands, like a bot without a party - but without warps of its own, which would spend what
        // it is saving - until it can afford the trip.
        var isSavingForFollowFare = leaderToFollow is not null && this.IsSavingForFollowFare(map, leaderToFollow);
        if (isSavingForFollowFare)
        {
            leaderToFollow = null;
        }

        var isTetheredToLeader = leaderToFollow is not null
            && ReferenceEquals(leaderToFollow.CurrentMap, map)
            && !inSafezone;

        // Followers hunt around their leader and never pick an independent ground. Dropping a claim
        // from before the bot joined the party also keeps it from blocking other bots.
        if (leaderToFollow is not null)
        {
            this._player.HuntingSpot = null;
        }

        // Combat centre: normally the bot's own position (self-defence while travelling and at the hunting
        // ground). A follower on the leader's map hunts tethered to the leader instead, so the combat
        // handler only ever picks targets near him - this holds continuously, not just on idle ticks.
        // While inside the safezone we aim it at the destination, so the combat handler does NOT
        // make the bot stand in town swinging at monsters just outside (which it can't damage from a safezone) -
        // it should simply walk out of town instead.
        this._player.HuntingOrigin = isTetheredToLeader
            ? leaderToFollow!.Position
            : inSafezone && this._hasDestination ? this._destination : this._player.Position;

        // Watchdog bookkeeping: remember when the bot last actually changed position.
        if (this._player.Position != this._lastPosition)
        {
            this._lastPosition = this._player.Position;
            this._lastMoveUtc = DateTime.UtcNow;
        }

        // Only monsters the bot would actually fight count (the combat AI ignores ones failing its
        // damage-based safety check), so a too-strong monster next to the bot neither stops its travel
        // nor suppresses the stuck watchdog.
        var monstersNearby = map.GetAttackablesInRange(this._player.Position, TravelStopRange)
            .OfType<Monster>()
            .Count(m => m.IsAlive && !m.IsAtSafezone() && m.SummonedBy is null && CombatHandler.IsSafeTarget(this._player, m.Definition));

        return new TickContext(inSafezone, leaderToFollow, isTetheredToLeader, isSavingForFollowFare, monstersNearby);
    }

    /// <summary>
    /// One evaluation tick: the bot decides what it does next, from what <see cref="PrepareTickAsync"/>
    /// gathered. The order of the branches is the priority order of the bot - an announced event meeting or
    /// a market shift outranks the errands, the errands outrank following the leader, and hunting is what
    /// happens when nothing else claimed the tick.
    /// </summary>
    /// <param name="cancellationToken">The token which aborts the tick on shutdown.</param>
    private async ValueTask EvaluateAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || this._player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !this._player.IsAlive
            || this._player.CurrentMap is not { } map
            || this._player.SelectedCharacter is null)
        {
            return;
        }

        // Inside a mini-game event the whole normal routine below is suspended - the event
        // dictates the pace, and every branch that could move the bot off the event map
        // (shopping, revenge, warping, ground picking) must not run.
        if (this._player.CurrentMiniGame is not null)
        {
            this.Claim(BotState.MiniGame, "mini-game event");
            await this.EvaluateInsideMiniGameAsync(map, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tick = await this.PrepareTickAsync(map).ConfigureAwait(false);

        // The bot counts as stuck when it has been frozen on the spot: quickly when there is nothing to
        // fight (a wedged walk or blocked path), and after a much longer grace when huntable monsters are
        // around - standing still next to monsters usually just means fighting, but if the position stays
        // frozen well beyond that, something is wedged (e.g. a monster in range that cannot be reached).
        var stuckTimeout = tick.MonstersNearby > 0 ? StuckWithMonstersTimeout : StuckTimeout;
        var stuck = DateTime.UtcNow - this._lastMoveUtc > stuckTimeout;
        if (stuck)
        {
            // Break free: pick a fresh hunting ground and walk to it now. Issuing the walk resets the wedged
            // walker, and a new destination avoids re-stalling on the same blocked path. A follower
            // regroups on its leader instead of roaming to a random ground - the leader decides where
            // the party hunts.
            this._lastMoveUtc = DateTime.UtcNow;
            this._emptyGroundSince = null;
            if (tick.LeaderToFollow is not null && ReferenceEquals(tick.LeaderToFollow.CurrentMap, map))
            {
                this.Claim(BotState.Recover, "wedged, walking back to the leader");
                await this.TravelTowardAsync(map, tick.LeaderToFollow.Position, cancellationToken).ConfigureAwait(false);
                return;
            }

            this.Claim(BotState.Recover, "wedged, picking a new ground");
            await this.PickGroundAndTravelAsync(map, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A walk (travel hop or local combat move) is already in progress.
        if (this._player.IsWalking)
        {
            this.Claim(BotState.Idle, "walk in progress");
            return;
        }

        // A bot which announced an opened event waits at the meeting point until the entrance closes
        // (see BotChatPlugIn): it said where it would be, so it must be found there. This outranks the
        // errands and the party follow below - and a due reset, which would change its level.
        if (await this.TryEventMeetingAsync(map, tick.InSafezone, cancellationToken).ConfigureAwait(false))
        {
            this.Claim(BotState.EventMeeting, "announced event meeting");
            return;
        }

        // A bot on a market shift sells the goods of its personal store at its stall (see
        // BotMerchantPlugIn) - its customers have to find it there, like the players of an event meeting.
        if (await this.TryMarketShiftAsync(map, cancellationToken).ConfigureAwait(false))
        {
            this.Claim(BotState.MarketShift, "market shift");
            return;
        }

        // On servers with the reset feature, a due character reset outranks the whole routine below:
        // the bot has reached the required level, so hunting on only gains it nothing until it resets.
        // It keeps fighting normally through the random grace delay, then resets and starts its next
        // cycle (see BotResetHandler).
        if (await this.TryResetCharacterAsync().ConfigureAwait(false))
        {
            this.Claim(BotState.Reset, "character reset");
            return;
        }

        // A master bot invests freshly earned master points (one per master level). Queued into the
        // MuHelper tick like the level-up progression - learning a skill mutates the skill list, which
        // must never happen while the combat handler enumerates it (see PendingBotActions). The master
        // evolution itself is handled by the feature plugin's maintenance pass because it restarts the bot.
        if (BotMasterHandler.HasMasterPointsToSpend(this._player))
        {
            this._player.PendingBotActions.Enqueue(() => BotMasterHandler.TrySpendMasterPointsAsync(this._player));
        }

        // A shopping trip (selling junk loot, restocking potions with Zen) runs before everything else,
        // so it completes even for party members - the follow logic below would otherwise pull them
        // back to the leader mid-trade.
        if (await this.TryShoppingAsync(map, tick.InSafezone, cancellationToken).ConfigureAwait(false))
        {
            this.Claim(BotState.Shopping, "town errand: selling and restocking");
            return;
        }

        // A buff trip (claiming the free defense/damage buff a dialog NPC like the Elf Soldier offers)
        // is an errand like shopping - it also completes even for party members, and does not mix with
        // the follow logic below pulling the bot back to the leader mid-visit.
        if (await this.TryBuffTripAsync(map, tick.InSafezone, cancellationToken).ConfigureAwait(false))
        {
            this.Claim(BotState.Buff, "town errand: claiming a buff");
            return;
        }

        // A chaos machine trip (upgrading a lucky +9..+14 piece at the goblin) is an errand like the
        // others: it completes even for party members.
        if (await this.TryChaosMachineTripAsync(map, tick.InSafezone, cancellationToken).ConfigureAwait(false))
        {
            this.Claim(BotState.ChaosMachine, "town errand: mixing at the chaos machine");
            return;
        }

        // Armed revenge (a player killed this bot; it respawned on the same map) overrides the
        // whole normal routine below, including following the party leader: the bot marches back to
        // the place of its death. A leader on a revenge march still drags its followers along - the
        // posse a wronged player would bring. Once the revenge is spent or times out, the bot falls
        // back into the routine (and a follower rejoins its group).
        if (await this.TryRevengeMarchAsync(map, cancellationToken).ConfigureAwait(false))
        {
            this.Claim(BotState.Revenge, "revenge march");
            return;
        }

        // Party members follow their leader instead of roaming on their own: to the leader's map when
        // it warped away (see TryHandleCrossMapLeaderFollowAsync), and around him while on the same map (see
        // EvaluateFollowerHuntingAsync - hunt within range while there is something to fight, regroup
        // into formation when idle). Followers never roam to independent hunting grounds, warp on
        // their own, or step down barren maps - the leader (the lowest-level member, so it only ever
        // picks maps the whole group can hunt on) decides where the party goes. Trade-off, by design:
        // if the leader idles in town or is wedged, its followers idle with him.
        if (tick.LeaderToFollow is not null)
        {
            if (await this.TryHandleCrossMapLeaderFollowAsync(map, tick.LeaderToFollow, cancellationToken).ConfigureAwait(false))
            {
                this.Claim(BotState.FollowLeader, "following the leader to his map");
                return;
            }

            this.Claim(BotState.FollowLeader, "following the leader");
            await this.EvaluateFollowerHuntingAsync(map, tick.LeaderToFollow, tick.InSafezone, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A bot whose map is no longer the best it could safely hunt warps away with priority - even
        // though trivial monsters are still around. Without this, the nearest-monster homing always
        // finds SOMETHING huntable on the starter map, the warp logic below is never reached, and
        // high-level bots farm level-5 mobs on Lorencia forever (with the experience penalty) instead
        // of moving on to their level's maps. TryWarpToBetterMapAsync itself only fires when another
        // map is meaningfully better (margin + cooldown), so a bot on an adequate map stays put.
        if (tick.LeaderToFollow is null
            && !tick.IsSavingForFollowFare
            && await this.TryWarpToBetterMapAsync().ConfigureAwait(false))
        {
            this.Claim(BotState.Travel, "warping to a better map");
            return;
        }

        // Inside the safezone the bot can't fight anyway, so it must keep walking out of town instead of
        // freezing at the gate when a monster is just outside. Only stop to fight once it has left the safezone.
        if (!tick.InSafezone && tick.MonstersNearby > 0)
        {
            this._emptyGroundSince = null;
            this.Claim(BotState.Hunt, "monsters nearby");
            return;
        }

        // Home in on a nearby live monster instead of walking to a random tile in a map-sized spawn area
        // (where the bot would almost never arrive within combat range of an actual monster). This is what
        // makes the bot converge on real monsters and actually fight. The bot walks toward the monster;
        // once within the combat range, the branch above takes over and the combat handler engages. This
        // also runs inside the safezone, so a bot in town heads straight for the monsters just outside the
        // gate instead of a random far-away spawn point. Only when nothing is loaded within the seek radius
        // (deep in town, or all nearby monsters too strong) does it fall back to the coarse spawn-area /
        // warp logic below to relocate.
        if (this.TryFindNearestMonsterGround(map, out var monsterGround))
        {
            this._destination = monsterGround;
            this._hasDestination = true;
            if (await this.TravelTowardAsync(map, monsterGround, cancellationToken).ConfigureAwait(false))
            {
                this._emptyGroundSince = null;
                this.Claim(BotState.Hunt, "walking to a monster");
                return;
            }

            // Unreachable monster (e.g. across a river): drop it and fall through to pick a spawn-area ground
            // or warp, instead of re-targeting the same unreachable monster every tick.
            this._hasDestination = false;
        }

        // Nothing to fight here. If we still have a destination to reach, keep walking towards it. If the
        // route turns out to be impossible (e.g. across a river), drop it and fall through to pick another
        // ground in this same tick instead of standing still.
        if (this._hasDestination && this._player.GetDistanceTo(this._destination) > HuntingRange)
        {
            if (!await this.TravelTowardAsync(map, this._destination, cancellationToken).ConfigureAwait(false))
            {
                // Impossible route: drop the destination and wait out the empty-ground grace before
                // picking another one. Re-picking immediately turned a pocket of unreachable picks
                // into a full-tick churn - a new far-away ground every second, hammering the path
                // finder without the bot ever moving.
                this._hasDestination = false;
                this._emptyGroundSince ??= DateTime.UtcNow;
            }
            else
            {
                this.Claim(BotState.Travel, "walking to the ground");
            }

            return;
        }

        // Arrived (or no destination) and the area is empty: wait out a short grace, then pick a new ground.
        // In the safezone we skip the grace so a freshly spawned bot heads out of town straight away.
        this._hasDestination = false;
        if (!tick.InSafezone)
        {
            this._emptyGroundSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - this._emptyGroundSince < EmptyGroundGrace)
            {
                this.Claim(BotState.Idle, "empty area, waiting for monsters");
                return;
            }
        }

        this._emptyGroundSince = null;

        if (tick.LeaderToFollow is null && !tick.IsSavingForFollowFare && await this.TryWarpToBetterMapAsync().ConfigureAwait(false))
        {
            this.Claim(BotState.Travel, "warping to a better map, nothing left here");
            return;
        }

        this.Claim(BotState.Hunt, "picked a new ground");
        await this.PickGroundAndTravelAsync(map, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the state the current tick puts the bot in - for the state command (see
    /// <see cref="BotStateMachine.Describe"/>) and, at debug level, for the log of a bot which has to be
    /// looked at later.
    /// </summary>
    /// <param name="state">The state the tick puts the bot in.</param>
    /// <param name="reason">Why the tick puts it there.</param>
    private void Claim(BotState state, string reason)
    {
        var previous = this._states.Current;
        this._states.Enter(state, reason);
        if (previous != state)
        {
            this._player.Logger.LogDebug("Bot {Character} is now {State}: {Reason}", this._player.Name, state, reason);
        }
    }

    /// <summary>
    /// Tells whether a routine state is being entered while the player still carries the flags of an
    /// unfinished errand - a navigation branch which bypassed the errand it left running (see
    /// <see cref="BotStateMachine.GuardViolations"/>).
    /// </summary>
    /// <param name="next">The state the tick is about to claim.</param>
    /// <returns><c>true</c>, if an errand is still unfinished.</returns>
    private bool IsErrandClaimed(BotState next)
        => !next.IsErrand() && !next.IsSuspended() && this.HasPendingErrand();

    /// <summary>
    /// Gets a value indicating whether the player carries the flags of an unfinished errand.
    /// </summary>
    private bool HasPendingErrand()
        => this._player.IsOnShoppingTrip
            || this._player.IsOnBuffTrip
            || this._player.IsOnChaosMachineTrip
            || this._player.MarketShift is not null
            || this._player.EventMeeting is not null;

    /// <summary>
    /// Drives a shopping trip: when the backpack fills up or potions run low, the bot heads to a town
    /// merchant (warping to the map's safezone first when out in the field, like using a town portal),
    /// walks up to it and trades - selling junk loot and restocking potions with Zen.
    /// </summary>
    /// <returns>True, if the shopping trip consumed this tick.</returns>
    private async ValueTask<bool> TryShoppingAsync(GameMap map, bool inSafezone, CancellationToken cancellationToken)
    {
        if (this._shoppingTarget is { } target)
        {
            if (BotTownTripPlanner.PlanTargetStep(this._player.GetDistanceTo(target)) == BotTownTripPlanner.TripStep.WalkToNpc)
            {
                if (!await this.TravelTowardAsync(map, target, cancellationToken).ConfigureAwait(false))
                {
                    // No way to the merchant from here - give up this trip and don't retry every
                    // check interval (an unreachable merchant stays unreachable for a while; a bot
                    // retrying twice a minute wastes its hunting time on futile marches).
                    this._player.Logger.LogDebug("Bot '{Name}' gives up its shopping trip: no route to the merchant at {Target}.", this._player.Name, target);
                    this._shoppingTarget = null;
                    this._player.IsOnShoppingTrip = false;
                    this._nextShoppingCheckUtc = NextShoppingCheckUtc();
                }

                return true;
            }

            // The trade itself runs in the MU Helper tick, not here: selling and buying mutate the
            // inventory and the Zen of a character whose combat tick may be running at this very moment
            // (the open NPC dialog only holds off the NEXT tick) - and both write through the same,
            // not-thread-safe persistence context. The same rule as for every other bot-initiated mutation,
            // see PendingBotActions. The jewels are spent right after the trade, in the same tick.
            this._player.PendingBotActions.Enqueue(() => this.TradeAndUpgradeAsync(map, target));

            this._shoppingTarget = null;
            this._player.IsOnShoppingTrip = false;
            this._nextShoppingCheckUtc = NextShoppingCheckUtc();
            this._lastMoveUtc = DateTime.UtcNow; // standing at the shop is not "stuck"
            return true;
        }

        if (DateTime.UtcNow < this._nextShoppingCheckUtc)
        {
            return false;
        }

        this._nextShoppingCheckUtc = DateTime.UtcNow + ShoppingCheckInterval;
        var needsShopping = BotShoppingHandler.NeedsShopping(this._player);
        var merchantPosition = needsShopping ? BotShoppingHandler.FindMerchantPosition(this._player, map) : null;
        ExitGate? homeGate = null;
        GameMapDefinition? homeMap = null;
        var homeGateAvailable = merchantPosition is null
                                && this.TryGetHomeEscapeGate(allowTrivialHome: true, out homeGate, out homeMap, out _);
        var townGate = merchantPosition is not null ? BotTownTripPlanner.FindTownGate(map.Definition) : null;
        var step = BotTownTripPlanner.PlanTrip(needsShopping, merchantPosition, inSafezone, townGate is not null, homeGateAvailable);
        if (step == BotTownTripPlanner.TripStep.NoErrandNeeded)
        {
            return false;
        }

        if (step == BotTownTripPlanner.TripStep.NoNpcOnMap)
        {
            // No merchant lives on this map at all (the Dungeon has no town) and there is no hometown to
            // go to either: shopping here would starve the bot's logistics for as long as it stays - no
            // potion restock, no selling, a slowly silting backpack.
            this._nextShoppingCheckUtc = NextShoppingCheckUtc();
            return false;
        }

        if (step == BotTownTripPlanner.TripStep.WarpHome)
        {
            // Like a player pulling a town scroll, it warps to its class hometown and shops there; the
            // better-map logic takes it back hunting afterward.
            this._travelPath = null;
            this._hasDestination = false;
            this._lastWarpUtc = this._timeProvider.GetUtcNow();
            this._nextShoppingCheckUtc = DateTime.UtcNow; // start the trip on the new map right away
            this._player.Logger.LogDebug("Bot '{Name}' warps home to {Map} for a shopping trip - no merchant on its map.", this._player.Name, homeMap!.Name);
            await this._player.WarpToAsync(homeGate!).ConfigureAwait(false);
            return true;
        }

        if (step == BotTownTripPlanner.TripStep.WarpToSafezone)
        {
            // Merchants stand in town: when out in the field, warp to the map's safezone first (like using
            // a town portal scroll), then walk over to the merchant.
            this._travelPath = null;
            this._hasDestination = false;
            await this._player.WarpToAsync(townGate!).ConfigureAwait(false);
        }

        this._shoppingTarget = merchantPosition;
        this._player.IsOnShoppingTrip = true;
        this._player.Logger.LogDebug("Bot '{Name}' heads to the merchant for a shopping trip.", this._player.Name);
        return true;
    }

    /// <summary>
    /// The trade at the merchant, as it runs inside the MU Helper tick: sell the junk, restock the
    /// potions and - still standing safely at the shop with the dialog closed - spend a few of the
    /// looted jewels on the own gear, the player-like moment for it (see <see cref="BotJewelHandler"/>).
    /// </summary>
    private async ValueTask TradeAndUpgradeAsync(GameMap map, Point merchantPosition)
    {
        if (await BotShoppingHandler.TryTradeAsync(this._player, map, merchantPosition).ConfigureAwait(false))
        {
            await BotJewelHandler.TryUpgradeGearAsync(this._player).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drives a buff trip: when the server has a dialog NPC offering a buff the bot qualifies for (the
    /// Elf Soldier grants fresh characters a defense and a damage buff, but exists only in Season Six
    /// data), the bot heads to it - warping to the map's safezone first when out in the field, like
    /// using a town portal - walks up to it and claims the buff like a real player would. Without data
    /// to configure the NPC the check simply never fires, so this is a no-op on earlier seasons.
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="inSafezone">Indicates whether the bot currently is in the safezone of the map.</param>
    /// <param name="cancellationToken">The token which aborts the travel wait on shutdown.</param>
    /// <returns>True, if the buff trip consumed this tick.</returns>
    private async ValueTask<bool> TryBuffTripAsync(GameMap map, bool inSafezone, CancellationToken cancellationToken)
    {
        if (this._buffTarget is { } target)
        {
            return await this.DriveBuffTripAsync(map, target, cancellationToken).ConfigureAwait(false);
        }

        if (DateTime.UtcNow < this._nextBuffCheckUtc)
        {
            return false;
        }

        this._nextBuffCheckUtc = DateTime.UtcNow + BuffCheckInterval;
        var needsBuff = BotBuffHandler.TryGetApplicableBuff(this._player) is not null;
        var buffNpcPosition = needsBuff ? BotBuffHandler.FindBuffNpcPosition(this._player, map) : null;

        // Only asked when the trip could need it: finding the hometown next to the buff check would
        // scan the hometown's spawn areas on every check of every bot.
        ExitGate? homeGate = null;
        GameMapDefinition? homeMap = null;
        var homeGateAvailable = buffNpcPosition is null
                                && this.TryGetHomeEscapeGate(allowTrivialHome: true, out homeGate, out homeMap, out _);
        var townGate = buffNpcPosition is not null ? BotTownTripPlanner.FindTownGate(map.Definition) : null;
        var step = BotTownTripPlanner.PlanTrip(needsBuff, buffNpcPosition, inSafezone, townGate is not null, homeGateAvailable);
        if (step == BotTownTripPlanner.TripStep.NoErrandNeeded)
        {
            // No buff dialog NPC configured (data without an Elf Soldier) or the bot does not qualify
            // (already buffed, or outside the buff's level range): a no-op on such servers.
            return false;
        }

        if (step == BotTownTripPlanner.TripStep.NoNpcOnMap)
        {
            this._nextBuffCheckUtc = NextBuffCheckUtc();
            return false;
        }

        if (step == BotTownTripPlanner.TripStep.WarpHome)
        {
            // No buff NPC lives on this map at all. Like a player pulling a town scroll, the bot warps
            // to its class hometown, where the buff is offered; the better-map logic takes it back
            // hunting afterward.
            this._travelPath = null;
            this._hasDestination = false;
            this._lastWarpUtc = this._timeProvider.GetUtcNow();
            this._nextBuffCheckUtc = DateTime.UtcNow; // start the trip on the new map right away
            this._player.Logger.LogDebug("Bot '{Name}' warps home to {Map} for its buff - no buff NPC on its map.", this._player.Name, homeMap!.Name);
            await this._player.WarpToAsync(homeGate!).ConfigureAwait(false);
            return true;
        }

        if (step == BotTownTripPlanner.TripStep.WarpToSafezone)
        {
            // Buff NPCs stand in town (in or near the safezone): when out in the field, warp to the
            // map's safezone first (like using a town portal scroll), then walk over to the NPC.
            this._travelPath = null;
            this._hasDestination = false;
            await this._player.WarpToAsync(townGate!).ConfigureAwait(false);
        }

        this._buffTarget = buffNpcPosition;
        this._player.IsOnBuffTrip = true;
        this._player.Logger.LogDebug("Bot '{Name}' heads to the buff NPC for its free buff.", this._player.Name);
        return true;
    }

    /// <summary>
    /// Drives a buff trip which already has its target: walks the bot to the buff NPC, or - when it stands
    /// there - queues the claim. A target which has no route is dropped, so a bot doesn't retry a futile
    /// march every check interval.
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="target">The position of the buff NPC.</param>
    /// <param name="cancellationToken">The token which aborts the travel wait on shutdown.</param>
    /// <returns>Always <c>true</c>: the trip consumed this tick.</returns>
    private async ValueTask<bool> DriveBuffTripAsync(GameMap map, Point target, CancellationToken cancellationToken)
    {
        if (BotTownTripPlanner.PlanTargetStep(this._player.GetDistanceTo(target)) == BotTownTripPlanner.TripStep.WalkToNpc)
        {
            if (!await this.TravelTowardAsync(map, target, cancellationToken).ConfigureAwait(false))
            {
                // No way to the buff NPC from here - give up this trip and don't retry every
                // check interval: an unreachable NPC stays unreachable for a while, and a bot
                // retrying twice a minute wastes its hunting time on futile marches.
                this._player.Logger.LogDebug("Bot '{Name}' gives up its buff trip: no route to the buff NPC at {Target}.", this._player.Name, target);
                this._buffTarget = null;
                this._player.IsOnBuffTrip = false;
                this._nextBuffCheckUtc = NextBuffCheckUtc();
            }

            return true;
        }

        // The dialog itself runs in the MU Helper tick, not here: it pauses the combat AI on the
        // next tick and applies the effect to the character, a player-state mutation which must run
        // where all other bot mutations run (see PendingBotActions).
        this._player.PendingBotActions.Enqueue(() => this.ClaimBuffAsync(map, target));

        this._buffTarget = null;
        this._player.IsOnBuffTrip = false;
        this._nextBuffCheckUtc = NextBuffCheckUtc();
        this._lastMoveUtc = DateTime.UtcNow; // standing at the buff NPC is not "stuck"
        return true;
    }

    /// <summary>
    /// Claims the buff at the buff NPC, as it runs inside the MU Helper tick: open the dialog, request
    /// the buff, close the dialog - the player-like visit, see <see cref="BotBuffHandler.TryRequestBuffAsync"/>.
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="buffNpcPosition">The position of the buff NPC.</param>
    private async ValueTask ClaimBuffAsync(GameMap map, Point buffNpcPosition)
    {
        await BotBuffHandler.TryRequestBuffAsync(this._player, map, buffNpcPosition).ConfigureAwait(false);
    }

    /// <summary>
    /// Warps the bot through a gate to its meeting, counting the warps through the meeting's own gate.
    /// </summary>
    private async ValueTask WarpToMeetingAsync(BotEventMeeting meeting, ExitGate gate)
    {
        if (ReferenceEquals(gate, meeting.Gate))
        {
            this._gateWarpCount = this.GetGateWarpCount(meeting) + 1;
            this._gateWarpMeeting = meeting;
        }

        this._travelPath = null;
        this._hasDestination = false;
        this._lastWarpUtc = this._timeProvider.GetUtcNow();
        this._player.Logger.LogDebug("Bot '{Name}' warps to {Map} to wait for players at the meeting point.", this._player.Name, meeting.Map.Name);
        await this._player.WarpToAsync(gate).ConfigureAwait(false);
    }

    private int GetGateWarpCount(BotEventMeeting meeting)
        => ReferenceEquals(this._gateWarpMeeting, meeting) ? this._gateWarpCount : 0;

    /// <summary>
    /// Drives a market shift (see <see cref="OfflinePlayer.MarketShift"/>): the bot warps to the market
    /// through its spawn gate - not through the warp list, whose level requirement and costs are for
    /// the players -, walks to its stall, opens its personal store and stays until the shift ends
    /// (see <see cref="BotMarketPlanner.GetEndReason"/>). Opening and closing the store run in the
    /// MU Helper tick, like the trades (see <see cref="OfflinePlayer.PendingBotActions"/>).
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="cancellationToken">The token which aborts the travel wait on shutdown.</param>
    /// <returns>True, if the shift consumed this tick.</returns>
    private async ValueTask<bool> TryMarketShiftAsync(GameMap map, CancellationToken cancellationToken)
    {
        if (this._player.MarketShift is not { } shift)
        {
            return await this.TryLeaveMarketAsync().ConfigureAwait(false);
        }

        // Standing behind the own store is not "stuck", and the hunting ground is free for others meanwhile.
        this._lastMoveUtc = DateTime.UtcNow;
        this._player.HuntingSpot = null;
        if (shift.IsEnding)
        {
            return true;
        }

        var configuration = this._player.GameContext.FeaturePlugIns.GetPlugIn<BotMerchantPlugIn>()?.Configuration;
        var endReason = configuration is null
            ? BotMarketPlanner.EndReason.TimeIsUp
            : BotMarketPlanner.GetEndReason(this._player, shift, configuration, DateTime.UtcNow);
        var onMarketMap = map.Definition.Number == shift.Map.Number;
        var gate = onMarketMap ? null : shift.Map.ExitGates.Where(g => g.IsSpawnGate).SelectRandom();
        var step = BotMarketPlanner.PlanShiftStep(
            endReason,
            onMarketMap,
            hasSpawnGate: !onMarketMap && gate is not null,
            atStall: this._player.GetDistanceTo(shift.Stall) <= 1,
            storeOpen: shift.IsStoreOpened,
            isOpenRequested: shift.IsOpenRequested);
        switch (step)
        {
            case BotMarketPlanner.ShiftStep.EndShift:
                this.EndMarketShift(shift, configuration, endReason == BotMarketPlanner.EndReason.None ? BotMarketPlanner.EndReason.StoreClosed : endReason);
                return true;

            case BotMarketPlanner.ShiftStep.WarpToMarket:
                this._travelPath = null;
                this._hasDestination = false;
                this._lastWarpUtc = this._timeProvider.GetUtcNow();
                this._player.Logger.LogDebug("Bot '{Name}' warps to {Map} for its market shift.", this._player.Name, shift.Map.Name);
                await this._player.WarpToAsync(gate!).ConfigureAwait(false);
                return true;

            case BotMarketPlanner.ShiftStep.WalkToStall:
                if (!await this.TravelTowardAsync(map, shift.Stall, cancellationToken).ConfigureAwait(false))
                {
                    this._player.Logger.LogDebug("Bot '{Name}' gives up its market shift: no route to its stall at {Stall}.", this._player.Name, shift.Stall);
                    this.EndMarketShift(shift, configuration, BotMarketPlanner.EndReason.StoreClosed);
                }

                return true;

            case BotMarketPlanner.ShiftStep.OpenStore:
                shift.IsOpenRequested = true;
                this._player.PendingBotActions.Enqueue(() => this.OpenMarketStoreAsync(shift));
                return true;

            case BotMarketPlanner.ShiftStep.Nothing:
            default:
                return true;
        }
    }

    /// <summary>
    /// Ends the market shift: the store gets closed in the next MU Helper tick, the shift is removed
    /// afterward, and the bot may start its next one after the configured time.
    /// </summary>
    private void EndMarketShift(BotMarketShift shift, BotMerchantConfiguration? configuration, BotMarketPlanner.EndReason reason)
    {
        shift.IsEnding = true;
        this._player.NextMarketShiftUtc = DateTime.UtcNow + (configuration?.TimeBetweenShifts ?? TimeSpan.Zero);
        this._player.Logger.LogDebug("Bot '{Name}' ends its market shift: {Reason}.", this._player.Name, reason);
        this._player.PendingBotActions.Enqueue(() => this.CloseMarketStoreAsync(shift));
    }

    /// <summary>
    /// Closes the personal store, removes the last income, retires the goods which found no buyer for
    /// too long and removes the shift, as it runs inside the MU Helper tick.
    /// </summary>
    private async ValueTask CloseMarketStoreAsync(BotMarketShift shift)
    {
        await BotMerchandise.CloseShiftStoreAsync(this._player, shift).ConfigureAwait(false);
        if (ReferenceEquals(this._player.MarketShift, shift))
        {
            this._player.MarketShift = null;
            this._leavesMarket = this._player.CurrentMap?.Definition.Number == shift.Map.Number;
        }
    }

    /// <summary>
    /// Takes the bot home after its market shift: the market has no hunting ground, so the routine
    /// wouldn't find a way back to the monsters from there.
    /// </summary>
    /// <returns>True, if the warp consumed this tick.</returns>
    private async ValueTask<bool> TryLeaveMarketAsync()
    {
        if (!this._leavesMarket)
        {
            return false;
        }

        this._leavesMarket = false;
        if (!this.TryGetHomeEscapeGate(allowTrivialHome: true, out var homeGate, out var homeMap, out _))
        {
            return false;
        }

        this._travelPath = null;
        this._hasDestination = false;
        this._lastWarpUtc = this._timeProvider.GetUtcNow();
        this._player.Logger.LogDebug("Bot '{Name}' warps home to {Map} after its market shift.", this._player.Name, homeMap.Name);
        await this._player.WarpToAsync(homeGate).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Ends the event meeting, unless a newer one replaced it in the meantime.
    /// </summary>
    /// <param name="meeting">The meeting which is over.</param>
    private void EndEventMeeting(BotEventMeeting meeting)
    {
        if (ReferenceEquals(this._player.EventMeeting, meeting))
        {
            this._player.EventMeeting = null;
        }
    }

    /// <summary>
    /// Drives a chaos machine trip: when the bot carries what an upgrade of a worn lucky piece needs (see
    /// <see cref="BotChaosMachineHandler.PlanNextMix"/>), it heads to the goblin - warping to a map with
    /// one first, through the regular warp action with its fare, when its own map has none (in Season Six
    /// only Noria does) - walks up to it and mixes. The mix itself runs in the MU Helper tick, like the
    /// trades and buffs (see <see cref="OfflinePlayer.PendingBotActions"/>).
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="inSafezone">Indicates whether the bot currently is in the safezone of the map.</param>
    /// <param name="cancellationToken">The token which aborts the travel wait on shutdown.</param>
    /// <returns>True, if the trip consumed this tick.</returns>
    private async ValueTask<bool> TryChaosMachineTripAsync(GameMap map, bool inSafezone, CancellationToken cancellationToken)
    {
        if (this._chaosMachineTarget is { } target)
        {
            if (BotTownTripPlanner.PlanTargetStep(this._player.GetDistanceTo(target)) == BotTownTripPlanner.TripStep.WalkToNpc)
            {
                if (!await this.TravelTowardAsync(map, this._chaosMachineStandingPoint, cancellationToken).ConfigureAwait(false))
                {
                    this._player.Logger.LogDebug("Bot '{Name}' gives up its chaos machine trip: no route to the goblin at {Target}.", this._player.Name, target);
                    this._chaosMachineTarget = null;
                    this._player.IsOnChaosMachineTrip = false;
                    this._nextChaosMachineCheckUtc = DateTime.UtcNow + ChaosMachineCooldown;
                }

                return true;
            }

            this._player.PendingBotActions.Enqueue(() => this.MixAtChaosMachineAsync(map, target));
            this._chaosMachineTarget = null;
            this._player.IsOnChaosMachineTrip = false;
            this._nextChaosMachineCheckUtc = DateTime.UtcNow + ChaosMachineCheckInterval;
            this._lastMoveUtc = DateTime.UtcNow; // standing at the goblin is not "stuck"
            return true;
        }

        if (DateTime.UtcNow < this._nextChaosMachineCheckUtc)
        {
            return false;
        }

        this._nextChaosMachineCheckUtc = DateTime.UtcNow + ChaosMachineCheckInterval;
        if (BotChaosMachineHandler.GetChaosMachine(this._player.GameContext.Configuration) is not { } goblin
            || BotChaosMachineHandler.PlanNextMix(this._player, goblin) is not { } plan)
        {
            return false;
        }

        if (BotChaosMachineHandler.FindChaosMachinePosition(map) is not { } goblinPosition)
        {
            if (this._timeProvider.GetUtcNow() - this._lastWarpUtc < WarpCooldown)
            {
                // Just warped (e.g. to its hunting map): wait, rather than bouncing between maps.
                return false;
            }

            if (this.TryGetChaosMachineWarp(plan.Price, out var warp))
            {
                this._travelPath = null;
                this._hasDestination = false;
                this._player.HuntingSpot = null;
                this._lastWarpUtc = this._timeProvider.GetUtcNow();
                this._nextChaosMachineCheckUtc = DateTime.UtcNow; // start the trip on the new map right away
                this._player.Logger.LogDebug("Bot '{Name}' warps to {Map} for the chaos machine.", this._player.Name, warp.Gate?.Map?.Name);
                await WarpAction.WarpToAsync(this._player, warp).ConfigureAwait(false);
                return true;
            }

            this._nextChaosMachineCheckUtc = DateTime.UtcNow + ChaosMachineCooldown;
            return false;
        }

        if (BotChaosMachineHandler.FindStandingPoint(map.Terrain, goblinPosition, BotTownTripPlanner.NpcInteractionRange) is not { } standingPoint)
        {
            this._nextChaosMachineCheckUtc = DateTime.UtcNow + ChaosMachineCooldown;
            return false;
        }

        // The goblin stands in town: when out in the field, warp to the map's safezone first. The trip
        // planner decides that, so the rule for "get to town" is the same one the shopping and buff trips
        // use - the goblin is on this map, so the home fallback can't apply.
        var townGate = BotTownTripPlanner.FindTownGate(map.Definition);
        if (BotTownTripPlanner.PlanTrip(needsErrand: true, goblinPosition, inSafezone, townGate is not null, canWarpHome: false)
            == BotTownTripPlanner.TripStep.WarpToSafezone)
        {
            this._travelPath = null;
            this._hasDestination = false;
            await this._player.WarpToAsync(townGate!).ConfigureAwait(false);
        }

        // The bot leaves its hunting ground for the trip; its claim would keep other bots off it meanwhile.
        this._player.HuntingSpot = null;
        this._chaosMachineTarget = goblinPosition;
        this._chaosMachineStandingPoint = standingPoint;
        this._player.IsOnChaosMachineTrip = true;
        this._player.Logger.LogDebug("Bot '{Name}' heads to the chaos machine.", this._player.Name);
        return true;
    }

    /// <summary>
    /// Mixes at the chaos machine, as it runs inside the MU Helper tick (see <see cref="BotChaosMachineHandler.TryMixAsync"/>).
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="goblinPosition">The position of the chaos machine NPC.</param>
    private async ValueTask MixAtChaosMachineAsync(GameMap map, Point goblinPosition)
    {
        if (!await BotChaosMachineHandler.TryMixAsync(this._player, map, goblinPosition).ConfigureAwait(false))
        {
            // A refused mix (e.g. the castle tax on top of the price) would fail the same way in 30 s.
            this._nextChaosMachineCheckUtc = DateTime.UtcNow + ChaosMachineCooldown;
        }
    }

    /// <summary>
    /// Gets a warp the bot may take to a map where a chaos machine NPC stands, and can afford while
    /// keeping the price of the mix - otherwise it arrives without the Zen to mix.
    /// </summary>
    /// <param name="mixPrice">The price of the planned mix.</param>
    /// <param name="warp">The warp.</param>
    /// <returns>True, if there is one.</returns>
    private bool TryGetChaosMachineWarp(long mixPrice, [MaybeNullWhen(false)] out WarpInfo warp)
    {
        warp = BotChaosMachineHandler.PickWarp(
            this._player.GameContext.Configuration.Maps.SelectMany(this.GetLegalWarps),
            this._player.Money,
            mixPrice);
        return warp is not null;
    }

    /// <summary>
    /// Marches the bot back to the place of its death while revenge is armed (see
    /// <see cref="OfflinePlayer.TryGetRevengeDestination"/>). The single attempt is spent as soon as
    /// the bot arrives or runs into its killer on the way - no extra combat logic is needed here,
    /// because the re-armed aggressor memory already keeps the combat handler prioritizing the
    /// killer, struck only when legal (see <see cref="BotPvpRules"/>).
    /// Monsters along the route do not stop the march (self-defense still engages ones right
    /// next to the bot); a bot that stopped to farm would never arrive within the revenge time.
    /// </summary>
    /// <param name="map">The current map.</param>
    /// <param name="cancellationToken">The token which aborts the travel wait on shutdown.</param>
    /// <returns>True, if the revenge march consumed this tick.</returns>
    private async ValueTask<bool> TryRevengeMarchAsync(GameMap map, CancellationToken cancellationToken)
    {
        if (!this._player.TryGetRevengeDestination(map.Definition, out var deathSite))
        {
            return false;
        }

        if (this._player.RecentAggressor is { } aggressor
            && ReferenceEquals(aggressor.CurrentMap, map)
            && this._player.GetDistanceTo(aggressor.Position) <= TravelStopRange)
        {
            // Ran into the killer before reaching the site - that is who we came for. Stop travelling
            // and let the combat handler (which already prioritizes the aggressor) take over.
            this._player.ExpireRevenge("the bot caught up with its killer");
            return true;
        }

        if (this._player.GetDistanceTo(deathSite) <= HuntingRange)
        {
            // Arrived. If the killer still stands here, the armed aggressor memory makes the combat
            // handler engage; otherwise the bot simply hunts on normally from this spot.
            this._player.ExpireRevenge("the bot arrived at the death site");
            return false;
        }

        this._destination = deathSite;
        this._hasDestination = true;
        if (!await this.TravelTowardAsync(map, deathSite, cancellationToken).ConfigureAwait(false))
        {
            // No walkable route back (e.g. the respawn gate is across a river) - drop the attempt
            // instead of re-issuing an impossible walk every tick.
            this._player.ExpireRevenge("no route back to the death site");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The bot's routine while it takes part in a mini-game event (Blood Castle, Devil Square,
    /// Chaos Castle): fight whatever the event throws at it, keep up with the party leader on the
    /// way through the course, and close in on the action when it moved away - nothing else. Death
    /// and the event's end need no handling here: the engine respawns a dead bot at the map's
    /// safezone like a player (which removes it from the event), and the event warps the remaining
    /// participants out when it ends.
    /// </summary>
    private async ValueTask EvaluateInsideMiniGameAsync(GameMap map, CancellationToken cancellationToken)
    {
        // Keep the drinking supplies topped up - an event without potions ends quickly.
        this._player.PendingBotActions.Enqueue(() => this.EnsurePotionsAsync());

        // Fight from wherever the bot stands; the combat handler engages the targets around it.
        this._player.HuntingOrigin = this._player.Position;

        if (this._player.IsWalking)
        {
            return;
        }

        // Stay with the leader on the way through the event's course (the Blood Castle bridge) ...
        if (this.GetPartyLeaderToFollow() is { } leader
            && ReferenceEquals(leader.CurrentMap, map)
            && this._player.GetDistanceTo(leader.Position) > FollowDistance)
        {
            await this.TravelTowardAsync(map, leader.Position, cancellationToken).ConfigureAwait(false);
            return;
        }

        // ... and otherwise close in on the nearest opposition instead of idling at the entrance
        // when the wave spawned (or the Chaos Castle crowd moved) beyond the combat range.
        if (!map.GetAttackablesInRange(this._player.Position, TravelStopRange).Any(this.IsEventTarget)
            && this.TryFindNearestEventTargetGround(map, out var ground))
        {
            await this.TravelTowardAsync(map, ground, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the object is something this bot would fight inside a mini-game event. Unlike in the
    /// open world, there is no safe-level filtering - the event's level bracket already matched the
    /// bot, and the event dictates the opposition. Players count as targets only where the event
    /// allows player killing (Chaos Castle, see <see cref="BotPvpRules"/>).
    /// </summary>
    private bool IsEventTarget(IAttackable attackable)
    {
        if (ReferenceEquals(attackable, this._player)
            || !attackable.IsAlive
            || attackable.IsAtSafezone())
        {
            return false;
        }

        return attackable switch
        {
            Monster monster => monster.SummonedBy is null && monster.Definition.ObjectKind == NpcObjectKind.Monster,
            Player player => BotPvpRules.IsLegalPvpTarget(this._player, player),
            _ => false,
        };
    }

    /// <summary>
    /// Finds the spot of a nearby opponent inside a mini-game event, mirroring
    /// <see cref="TryFindNearestMonsterGround"/> (random among the two nearest, so many bots
    /// don't dogpile the same target).
    /// </summary>
    private bool TryFindNearestEventTargetGround(GameMap map, out Point ground)
    {
        ground = default;
        var position = this._player.Position;
        var candidates = map.GetAttackablesInRange(position, MonsterSeekRadius)
            .Where(this.IsEventTarget)
            .OrderBy(a => a.GetDistanceTo(position))
            .Take(2)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        ground = candidates.SelectRandom()!.Position;
        return true;
    }

    /// <summary>
    /// Gets the party leader this bot should follow, or null if the bot is solo, is the leader itself,
    /// or the leader is currently not followable (dead, no map, or inside another mini-game instance -
    /// following into an event goes through the regular entry of <see cref="BotMiniGameHandler"/>,
    /// never through a warp).
    /// </summary>
    private Player? GetPartyLeaderToFollow()
    {
        if (this._player.Party is not { } party
            || party.PartyMaster is not Player leader
            || ReferenceEquals(leader, this._player)
            || !leader.IsAlive
            || leader.CurrentMap is null
            || !ReferenceEquals(leader.CurrentMiniGame, this._player.CurrentMiniGame))
        {
            return null;
        }

        return leader;
    }

    /// <summary>
    /// Performs the bot's character reset once it is due (reset feature enabled, required level
    /// reached, reset limit not exhausted). The reset is not executed the moment the bot becomes
    /// eligible: a random grace delay (<see cref="MinResetDelay"/>..<see cref="MaxResetDelay"/>) is
    /// rolled first, during which the bot hunts on normally. After the reset, the navigation state is
    /// cleared, so the bot starts its next cycle fresh - re-investing its points (queued by the reset
    /// handler) and heading for a hunting ground that suits it again.
    /// </summary>
    /// <returns>True, if a reset was performed.</returns>
    private async ValueTask<bool> TryResetCharacterAsync()
    {
        if (BotPartyHandler.HasHumanCompanion(this._player))
        {
            // Not in the middle of a hunting session with a player: the reset would wipe the bot's
            // strength and teleport it home, deserting the group. It resets once the party with the
            // human companion ends.
            return false;
        }

        if (BotResetHandler.GetResetConfiguration(this._player.GameContext) is not { } resetConfiguration
            || !BotResetHandler.IsResetDue(this._player, resetConfiguration))
        {
            this._resetDueAtUtc = null;
            return false;
        }

        if (this._resetDueAtUtc is not { } dueAt)
        {
            var delay = MinResetDelay + TimeSpan.FromSeconds(Rand.NextInt(0, (int)(MaxResetDelay - MinResetDelay).TotalSeconds + 1));
            this._resetDueAtUtc = DateTime.UtcNow + delay;
            this._player.Logger.LogDebug(
                "Bot {Character} reached the reset level; resetting in {Delay}.",
                this._player.Name,
                delay);
            return false;
        }

        if (DateTime.UtcNow < dueAt)
        {
            return false;
        }

        this._resetDueAtUtc = null;
        var payCosts = this._player.GameContext.FeaturePlugIns.GetPlugIn<BotFeaturePlugIn>()?.Configuration?.BotsPayResetCosts == true;

        // Queued into the MuHelper tick: the reset writes the level/reset/stat attributes, and such
        // mutations must not race the combat handler's recalculations (see PendingBotActions). The
        // queued call re-validates the eligibility itself; if it refuses (e.g. unaffordable costs
        // when BotsPayResetCosts is enabled), the check above re-schedules a fresh grace delay.
        var player = this._player;
        player.PendingBotActions.Enqueue(async () =>
        {
            await BotResetHandler.TryResetAsync(player, resetConfiguration, payCosts).ConfigureAwait(false);
        });

        // Start the next cycle fresh: no stale destination or route, and no warp cooldown - a freshly
        // reset player heads straight back out, and the point pool (queued by the reset handler into
        // the bot's AI tick) is invested before the next evaluation gets this far. Cleared
        // optimistically - if the queued reset refuses, the bot merely re-picks its hunting ground.
        this._hasDestination = false;
        this._travelPath = null;
        this._emptyGroundSince = null;
        this._lastWarpUtc = DateTimeOffset.MinValue;
        this._nextFollowWarpSearchUtc = DateTimeOffset.MinValue;
        return true;
    }

    /// <summary>
    /// Warps to the map that offers the strongest monsters this bot can still safely handle, so it
    /// earns the best experience without being slaughtered. It arrives at the destination safezone
    /// and walks out to a hunting ground like a real player.
    /// </summary>
    /// <returns>True, if a warp was performed.</returns>
    private async ValueTask<bool> TryWarpToBetterMapAsync()
    {
        if (this._player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || this._player.SelectedCharacter is null)
        {
            return false;
        }

        var botLevel = this.GetBotLevel();
        var plainLevel = (int)(this._player.Attributes?[Stats.Level] ?? 1);
        var currentMap = this._player.CurrentMap?.Definition;
        var currentLevel = currentMap is null ? 0 : this.BestSafeLevel(currentMap, 0, this.GetCurrentZoneFilter());
        var plan = BotMapChoicePlanner.PlanMove(new BotMapChoicePlanner.MapMoveFacts
        {
            BotLevel = botLevel,
            PlainLevel = plainLevel,
            MinWarpLevel = this.GetMinWarpLevel(),
            CurrentGroundLevel = currentLevel,
            HasCurrentMap = currentMap is not null,
            HasLegalWarpHere = currentMap is not null && this.TryGetLegalWarp(currentMap, out _),
            IsHomeMap = currentMap?.Number == this._player.SelectedCharacter?.CharacterClass?.HomeMap?.Number,
            EscapeReviewDeferred = this.IsEscapeReviewDeferred(botLevel, currentMap),
            Barren = DateTime.UtcNow - this._player.LastAttackUtc > BarrenMapDuration,
            WarpCooldownElapsed = this._timeProvider.GetUtcNow() - this._lastWarpUtc >= WarpCooldown,
        });
        if (plan is not { } move)
        {
            return false;
        }

        // When escaping, any legal map above the escape floor beats staying. A bot which cannot afford a
        // single warp - or is below every warp requirement, as a freshly reset veteran is - would
        // otherwise be stranded on ground that pays it nothing, unable to earn the fare out of it. The
        // map gates are free, like a player taking the road to the next map: a bot which can't pay the
        // fare walks through one, but only to a map that is really better than where it stands. Its home
        // map is the last resort - and for a high-level bot it is exactly the starter ground this is
        // meant to escape from, so the gate comes first, and going home is only an option when there is
        // nothing safe to fight where the bot stands.
        WarpInfo? fare = null;
        ExitGate targetGate;
        GameMapDefinition? targetMap;
        int targetLevel;
        var usedGateEscape = false;
        if ((move.StepDown && this.TryPickEasierMap(out var easierWarp, out targetMap, out targetLevel))
            || this.TryPickBetterMap(move.Escape, move.EscapeFloor, out easierWarp, out targetMap, out targetLevel))
        {
            fare = easierWarp;
            targetGate = easierWarp.Gate!;
        }
        else if (currentMap is not null
                 && (move.Escape || move.StepDown)
                 && this.TryGetGateEscape(currentMap, currentLevel, out var gate, out targetMap, out targetLevel))
        {
            targetGate = gate;
            usedGateEscape = true;
        }
        else if ((move.Escape || move.StepDown)
                 && this.TryGetHomeEscapeGate(allowTrivialHome: move.AllowTrivialHome, out var homeGate, out targetMap, out targetLevel))
        {
            targetGate = homeGate;
        }
        else
        {
            // Nowhere better to go. A bot on trivial ground settles for it and earns its way up, instead
            // of walking to the next equally trivial ground and back on every warp cooldown.
            if (move.Trivial)
            {
                this.DeferEscapeReview(botLevel, currentMap);
                this._player.Logger.LogDebug(
                    "Bot {Character} (level {Level}) stays on map {Map}: its monsters (~{MonsterLevel}) are trivial, but nothing within reach offers better ground than that.",
                    this._player.Name,
                    botLevel,
                    currentMap?.Name,
                    currentLevel);
            }

            return false;
        }

        // The barren timer restarts with the move: the new map deserves the same chance to prove itself
        // before it is judged, and without this every following tick would warp again.
        this._player.LastAttackUtc = DateTime.UtcNow;
        this._lastWarpUtc = this._timeProvider.GetUtcNow();
        this._hasDestination = false;
        this._travelPath = null;
        this._player.HuntingSpot = null;
        this._escapeReview = default;
        this._player.Logger.LogDebug(
            "Bot {Character} (level {Level}) warping to map {Map} through {Warp} (monsters ~{MonsterLevel}, fare {Fare} zen, current ground ~{CurrentLevel}: {Reason}).",
            this._player.Name,
            botLevel,
            targetMap!.Name,
            fare?.ToString() ?? (usedGateEscape ? "a map gate" : "its home gate"),
            targetLevel,
            fare?.Costs ?? 0,
            currentLevel,
            BotMapChoicePlanner.Describe(move.Reason));

        if (fare is not null)
        {
            // Through the player action, so the bot pays the fare and passes every check the game makes
            // of a player warping there - including any this navigator does not know about.
            await WarpAction.WarpToAsync(this._player, fare).ConfigureAwait(false);
        }
        else
        {
            await this._player.WarpToAsync(targetGate).ConfigureAwait(false);
        }

        // Where the character ACTUALLY ended up, not where it was sent: the warp action re-checks what
        // the candidate filter checked and may refuse, and remembering a refused trip as a move would
        // reload the bot onto a map it never reached.
        await this.TryPersistCurrentMapAsync(this._player.CurrentMap?.Definition ?? targetMap).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Walks the next stretch towards the destination. A full route is resolved with a heuristic search
    /// (so it goes around walls and out of the town gate) and the first <see cref="StepsPerHop"/> steps are
    /// issued; the remainder is recomputed from the new position on the next tick. The safe zone is allowed
    /// in the path so the bot can leave town.
    /// </summary>
    private async ValueTask<bool> TravelTowardAsync(GameMap map, Point destination, CancellationToken cancellationToken)
    {
        var position = this._player.Position;

        // Follow the cached route as long as it still leads to (roughly) the same destination and the
        // bot is still on it. Re-planning a whole-map A* every tick just to consume three steps wasted
        // CPU and starved the shared pathfinder pool once dozens of bots traveled at the same time.
        if (this._travelPath is { } cached
            && this._travelPathIndex < cached.Count
            && destination.EuclideanDistanceTo(this._travelPathTarget) <= PathReuseTolerance
            && position.EuclideanDistanceTo(cached[this._travelPathIndex].Point) <= 1.5)
        {
            await this.WalkCachedStepsAsync(position).ConfigureAwait(false);
            return true;
        }

        this._travelPath = null;

        var path = await this.FindTravelPathAsync(map, position, destination, cancellationToken).ConfigureAwait(false);

        if (path is null || path.Count == 0)
        {
            // No route to this ground (e.g. across a river): report it so the navigator picks another one
            // straight away instead of standing still re-issuing an impossible walk.
            return false;
        }

        this._travelPath = path;
        this._travelPathIndex = 0;
        this._travelPathTarget = destination;
        await this.WalkCachedStepsAsync(position).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<IList<PathResultNode>?> FindTravelPathAsync(
        GameMap map,
        Point start,
        Point destination,
        CancellationToken cancellationToken)
    {
        // Observe the navigator's shutdown token while queuing for a shared pathfinder: on disposal the
        // wait is abandoned at once instead of holding the tick until a finder frees up. Cancellation from
        // this wait or FindPath is expected during shutdown and handled by SafeEvaluateAsync.
        await TravelPathFinderPool.WaitAsync(cancellationToken).ConfigureAwait(false);
        PathFinder? finder = null;
        try
        {
            if (!TravelPathFinders.TryTake(out finder))
            {
                finder = CreateTravelPathFinder();
            }

            finder.ResetPathFinder();
            return finder.FindPath(start, destination, map.Terrain.AIgrid, true, cancellationToken);
        }
        finally
        {
            if (finder is not null)
            {
                TravelPathFinders.Add(finder);
            }

            TravelPathFinderPool.Release();
        }
    }

    /// <summary>
    /// Issues the next few steps of the cached route (short hops, so the bot can stop and fight between
    /// them) and advances the route cursor.
    /// </summary>
    private async ValueTask WalkCachedStepsAsync(Point position)
    {
        var path = this._travelPath!;
        var stepsCount = Math.Min(path.Count - this._travelPathIndex, StepsPerHop);
        var steps = new WalkingStep[stepsCount];
        for (var i = 0; i < stepsCount; i++)
        {
            var node = path[this._travelPathIndex + i];
            var previous = i == 0 ? position : steps[i - 1].To;
            steps[i] = new WalkingStep(previous, node.Point, previous.GetDirectionTo(node.Point));
        }

        this._travelPathIndex += stepsCount;
        await this._player.WalkToAsync(steps[stepsCount - 1].To, steps).ConfigureAwait(false);

        // A walk refused by the game (e.g. the bot is frozen) must not trigger a decision on every timer shot.
        this._isHopInProgress = this._player.IsWalking;
    }

    /// <summary>
    /// Ensures the bot keeps a stack of healing and a stack of mana potions in its inventory. Creates the
    /// stacks the first time (covering bots generated before they were granted) and refills them once they
    /// run low, so the bot never goes without a way to heal or to keep casting.
    /// </summary>
    private async ValueTask EnsurePotionsAsync()
    {
        await this.EnsurePotionStackAsync(HealPotionNumber).ConfigureAwait(false);
        await this.EnsurePotionStackAsync(ManaPotionNumber).ConfigureAwait(false);
        await this.EnsureAmmunitionAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps an archer's ammunition topped up. With the starter bow the offline attack path does not
    /// consume arrows, but an upgraded bow can carry an ammunition consumption rate - without this the
    /// bot would eventually shoot its quiver empty and its bow skills would stop working.
    /// </summary>
    private async ValueTask EnsureAmmunitionAsync()
    {
        if (this._player.Inventory is not { } inventory)
        {
            return;
        }

        if (inventory.EquippedAmmunitionItem is { } ammo)
        {
            if (ammo.Durability < HealPotionRefillBelow && ammo.Definition is { } ammoDefinition)
            {
                ammo.Durability = Math.Max((byte)1, ammoDefinition.Durability);
            }

            return;
        }

        // Ammo ran out completely (an empty stack gets destroyed): if a bow is equipped, put a fresh
        // stack of arrows into the other hand.
        var bow = inventory.EquippedItems.FirstOrDefault(i =>
            i.Definition is { Group: 4, IsAmmunition: false });
        if (bow is null)
        {
            return;
        }

        var arrows = this._player.GameContext.Configuration.Items
            .FirstOrDefault(d => d.Group == 4 && d.Number == 15);
        if (arrows is null)
        {
            return;
        }

        var item = this._player.PersistenceContext.CreateNew<Item>();
        item.Definition = arrows;
        item.Durability = Math.Max((byte)1, arrows.Durability);
        var targetSlot = bow.ItemSlot == InventoryConstants.RightHandSlot
            ? InventoryConstants.LeftHandSlot
            : InventoryConstants.RightHandSlot;
        if (!await inventory.AddItemAsync(targetSlot, item).ConfigureAwait(false))
        {
            await this._player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsurePotionStackAsync(int potionNumber)
    {
        if (this._player.Inventory is not { } inventory
            || this._player.GameContext.Configuration.Items
                .FirstOrDefault(d => d.Group == HealPotionGroup && d.Number == potionNumber) is not { } definition)
        {
            return;
        }

        var potions = inventory.Items
            .Where(i => i.Definition?.Group == HealPotionGroup && i.Definition.Number == potionNumber)
            .ToList();
        var charges = potions.Sum(p => (int)p.Durability);
        if (charges >= HealPotionRefillBelow)
        {
            return;
        }

        // Only ever up to the emergency amount, never a full stack: a Large Healing Potion holds 255
        // charges, four times what a bot stocks at a merchant, so handing out whole stacks here made the
        // fallback the bot's normal potion supply and the shopping trip never had a reason to restock.
        var stackSize = Math.Min(Math.Max((byte)1, definition.Durability), (byte)EmergencyPotionCharges);
        foreach (var potion in potions.Where(p => p.Durability < stackSize))
        {
            charges += (int)(stackSize - potion.Durability);
            potion.Durability = stackSize;
            if (charges >= EmergencyPotionCharges)
            {
                return;
            }
        }

        while (charges < EmergencyPotionCharges)
        {
            var item = this._player.PersistenceContext.CreateNew<Item>();
            item.Definition = definition;
            item.Durability = stackSize;
            if (!await inventory.AddItemAsync(item).ConfigureAwait(false))
            {
                // Backpack full - drop the throw-away item again; what is stocked has to do.
                await this._player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
                return;
            }

            charges += stackSize;
        }
    }

    /// <summary>
    /// Re-resolves the warp destination map through the bot's own persistence context and assigns that
    /// instance to the character. The warp itself assigns the GLOBAL configuration instance, which the
    /// bot's context does not track - the CurrentMapId foreign key silently never got saved, so a
    /// restarted bot always woke up on its home map and warped all over again (warp burst on restart).
    /// </summary>
    private async ValueTask TryPersistCurrentMapAsync(GameMapDefinition mapDefinition)
    {
        try
        {
            if (this._player.SelectedCharacter is not { } character)
            {
                return;
            }

            var ownInstance = await this._player.PersistenceContext
                .GetByIdAsync<GameMapDefinition>(mapDefinition.GetId()).ConfigureAwait(false);
            if (ownInstance is not null)
            {
                character.CurrentMap = ownInstance;
            }
        }
        catch (Exception ex)
        {
            // Not worth failing the warp over - worst case the map is (as before) not persisted.
            this._player.Logger.LogDebug(ex, "Could not persist current map for bot {Character}.", this._player.Name);
        }
    }

    /// <summary>
    /// The bot's reset-aware effective level (see <see cref="BotResetHandler.GetEffectiveLevel"/>),
    /// used for hunting decisions (which monsters pay off). Map ACCESS is deliberately not decided
    /// by this - the game gates warps by the plain character level (see <see cref="TryGetLegalWarp"/>),
    /// and the bots must obey the same rule.
    /// </summary>
    private int GetBotLevel() => BotResetHandler.GetEffectiveLevel(this._player);

    /// <summary>
    /// Gets the gate through which the bot may legally warp to the given map, exactly like a player
    /// using the in-game warp window: the map must have an entry in the server's warp list whose level
    /// requirement (class-reduced, see <see cref="CharacterExtensions.GetEffectiveMoveLevelRequirement"/>)
    /// is met by the bot's PLAIN character level. The reset-aware effective level intentionally plays
    /// no role here - a freshly reset veteran cannot warp to high maps either, no matter its strength.
    /// </summary>
    /// <param name="mapDefinition">The target map.</param>
    /// <param name="warp">The warp info, if a legal entry exists.</param>
    /// <returns>True, if the bot may warp to the map.</returns>
    private bool TryGetLegalWarp(GameMapDefinition mapDefinition, [MaybeNullWhen(false)] out WarpInfo warp)
    {
        warp = this.GetLegalWarps(mapDefinition)
            .FirstOrDefault();
        return warp is not null;
    }

    /// <summary>
    /// Gets the legal warp entry the bot can afford whose complete landing area has the shortest walkable
    /// route to a target.
    /// </summary>
    /// <param name="map">The target map.</param>
    /// <param name="target">The target position.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The best reachable warp, or <c>null</c> if no legal gate can reach the target.</returns>
    /// <remarks>
    /// A candidate is deliberately rejected when any coordinate which
    /// <see cref="ExitGateExtensions.GetRandomPoint(ExitGate)"/> may select is blocked or cannot reach
    /// the target. <see cref="Player.WarpToAsync(ExitGate)"/> performs that random placement once and
    /// does not retry or validate it, so accepting a partially usable gate would make following
    /// probabilistic. This fail-closed policy can reject a configured gate with many usable coordinates
    /// and one bad coordinate; keeping that trade-off local avoids changing map-transition behavior for
    /// other warp features.
    /// The reverse flood fill can inspect the whole map, whereas the travel pathfinder stops after
    /// <see cref="BotWarpPlanner.TravelSearchLimit"/> expanded nodes. A gate whose shortest landing route already exceeds
    /// that budget is therefore rejected before warping.
    /// </remarks>
    private WarpInfo? FindBestReachableLegalWarp(GameMap map, Point target, CancellationToken cancellationToken)
        => BotWarpPlanner.FindBestReachableWarp(
            this.GetLegalWarps(map.Definition).Where(this.CanAffordWarp),
            map.Terrain.WalkMap,
            target,
            ReferenceEquals(this._player.CurrentMap, map) ? this._player.Position : null,
            false,
            cancellationToken);

    private IEnumerable<WarpInfo> GetLegalWarps(GameMapDefinition mapDefinition)
    {
        if (this._player.SelectedCharacter is not { } character
            || BotFeaturePlugIn.GetConfiguration(this._player.GameContext)?.IsMapForbidden(mapDefinition) == true)
        {
            // A map forbidden for bots has no legal warp at all: no hunting ground, no leader to follow
            // there, no errand - and a bot which logged in on it treats it as illegal and leaves.
            return [];
        }

        // Runtime/instanced maps are not guaranteed to reference the same persisted entity as the
        // global warp list, so map-number matching remains the compatibility fallback. When the exact
        // definition is present, however, use only its entries to avoid mixing distinct definitions
        // which happen to share a number.
        // Warps which aren't warpable are refused for players (see WarpAction), so bots don't pick them.
        var sameNumberWarps = this._player.GameContext.Configuration.WarpList
            .Where(w => w.IsWarpable && w.Gate?.Map?.Number == mapDefinition.Number)
            .ToArray();
        var mapDefinitionId = mapDefinition.GetId();
        var exactMapWarps = mapDefinitionId == Guid.Empty
            ? []
            : sameNumberWarps.Where(w => w.Gate!.Map!.GetId() == mapDefinitionId).ToArray();
        var mapWarps = exactMapWarps.Length > 0 ? exactMapWarps : sameNumberWarps;

        // Select the identity tier before applying access requirements. Otherwise an exact entry
        // which is too high-level could incorrectly fall through to an easier same-number entry.
        var plainLevel = (int)(this._player.Attributes?[Stats.Level] ?? 1);
        return mapWarps.Where(w => character.GetEffectiveMoveLevelRequirement(w.LevelRequirement) <= plainLevel);
    }

    /// <summary>
    /// Lowest level at which this bot warps on its own: its lowest
    /// class-reduced warp requirement, so special classes keep their edge.
    /// Cached per configuration and class; both change rarely.
    /// </summary>
    private int GetMinWarpLevel()
    {
        var configuration = this._player.GameContext.Configuration;
        var characterClass = this._player.SelectedCharacter?.CharacterClass;
        if (!ReferenceEquals(configuration, this._warpFloorConfiguration)
            || !ReferenceEquals(characterClass, this._warpFloorClass))
        {
            this._warpFloorConfiguration = configuration;
            this._warpFloorClass = characterClass;
            this._minWarpLevel = configuration.WarpList
                .Select(warp => this._player.SelectedCharacter?.GetEffectiveMoveLevelRequirement(warp.LevelRequirement) ?? warp.LevelRequirement)
                .DefaultIfEmpty(FallbackMinWarpLevel)
                .Min();
        }

        return this._minWarpLevel;
    }

    /// <summary>
    /// Whether the bot can pay the warp's fare. A bot travels on the same terms as a player - it pays
    /// what the warp costs - so a map it cannot afford is not offered to it in the first place, rather
    /// than chosen and then refused by <see cref="WarpAction"/>.
    /// </summary>
    /// <param name="warp">The warp to pay for.</param>
    private bool CanAffordWarp(WarpInfo warp) => this._player.Money >= warp.Costs;

    /// <summary>
    /// The lowest monster level that still earns this bot anything: a master-class character at the
    /// game's maximum level gains master experience only from monsters of at least
    /// <c>GameConfiguration.MinimumMonsterLevelForMasterExperience</c> (the experience path grants it
    /// nothing at all below that) and no regular experience either, because it is at the maximum level
    /// already. Hunting below that floor is therefore
    /// not the safe choice for a mastered bot, it is a dead end - so the map and hunting ground choice
    /// look above the floor first. 0 for every other bot, which hunts by safety alone.
    /// </summary>
    private int MasterExperienceFloor()
    {
        var configuration = this._player.GameContext.Configuration;
        if (this._player.SelectedCharacter?.CharacterClass?.IsMasterClass != true
            || (this._player.Attributes?[Stats.Level] ?? 0) < configuration.MaximumLevel)
        {
            return 0;
        }

        return configuration.MinimumMonsterLevelForMasterExperience;
    }

    /// <summary>
    /// The level of the strongest monster on the map which this bot can safely fight (judged by the
    /// monster's real damage against the bot's defense and health, see <see cref="CombatHandler.IsSafeTarget"/>);
    /// 0 if the map has none. The level of the safe monsters remains the measure of a map's experience
    /// value when comparing maps - it just no longer decides what is SAFE. Monsters below
    /// <paramref name="minimumLevel"/> are not counted at all, which lets a mastered bot judge a map by
    /// what it actually earns there (see <see cref="MasterExperienceFloor"/>).
    /// </summary>
    /// <param name="mapDefinition">The map to judge.</param>
    /// <param name="minimumLevel">The lowest monster level worth counting.</param>
    /// <param name="isWithin">Limits the judgement to a part of the map (see <see cref="BotMapRegions"/> and
    /// <see cref="BotMapZones"/>); <c>null</c> to judge the whole map.</param>
    private int BestSafeLevel(GameMapDefinition mapDefinition, int minimumLevel = 0, Func<MonsterSpawnArea, bool>? isWithin = null)
    {
        var best = 0;
        foreach (var area in mapDefinition.MonsterSpawns)
        {
            if (!BotHuntingGroundSelector.IsPermanentMonsterSpawn(area)
                || (isWithin is not null && !isWithin(area)))
            {
                continue;
            }

            var level = BotHuntingGroundSelector.GetMonsterLevel(area.MonsterDefinition!);
            if (level >= minimumLevel && level > best && CombatHandler.IsSafeTarget(this._player, area.MonsterDefinition!))
            {
                best = level;
            }
        }

        return best;
    }

    /// <summary>
    /// Gets a filter for the monsters the bot can walk to from where it stands (see <see cref="BotMapRegions"/>).
    /// </summary>
    /// <returns>The filter, or <c>null</c> if the bot is on no map or stands on a tile which is not walkable.</returns>
    private Func<MonsterSpawnArea, bool>? GetCurrentRegionFilter()
    {
        if (this._player.CurrentMap?.Definition is not { } current
            || BotMapRegions.Get(current) is not { } regions
            || regions.GetRegion(this._player.Position) is not (var region and not 0))
        {
            return null;
        }

        int[] reachable = [region];
        return area => regions.IsAreaWithin(area, reachable, BotHuntingGroundSelector.GroundScatterSpan, HuntingRange);
    }

    /// <summary>
    /// Gets a filter for the monsters of the zone the bot stands in (see <see cref="BotMapZones"/>), so
    /// its current place is compared with the zones of the other entries on equal terms. A zone the bot
    /// keeps away from holds nothing for it. Outside of every zone, it's the reachable part of the map.
    /// </summary>
    /// <returns>The filter, or <c>null</c> to judge the whole map.</returns>
    private Func<MonsterSpawnArea, bool>? GetCurrentZoneFilter()
    {
        if (this._player.CurrentMap?.Definition is not { } current
            || this.GetHuntingZones(current) is not { } hunting
            || hunting.Zones.GetZone(this._player.Position) is not (var zone and >= 0))
        {
            return this.GetCurrentRegionFilter();
        }

        if (this._dangerZones.IsAvoided(hunting.Entries[zone]))
        {
            return _ => false;
        }

        return area => hunting.Zones.IsAreaWithin(area, zone, BotHuntingGroundSelector.GroundScatterSpan, HuntingRange);
    }

    /// <summary>
    /// Gets the zones of a map for the warp entries the bot may legally use (see <see cref="BotMapZones"/>).
    /// </summary>
    /// <param name="mapDefinition">The map.</param>
    /// <returns>The zones with their entries, in the same order; <c>null</c>, if the bot may use no entry.</returns>
    private (BotMapZones Zones, IReadOnlyList<WarpInfo> Entries)? GetHuntingZones(GameMapDefinition mapDefinition)
    {
        var entries = this.GetLegalWarps(mapDefinition).Where(w => w.Gate is not null).ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        return (BotMapRegions.Get(mapDefinition).GetZones(entries.Select(w => w.Gate!).ToList()), entries);
    }

    private void OnDied(object? sender, DeathInformation death)
    {
        // Only monsters: a player's kills are handled by the revenge and death site logic of the
        // OfflinePlayer, and an event death is part of the game.
        if (this._player.CurrentMiniGame is null
            && this._player.CurrentMap is { } map
            && map.GetObject(death.KillerId) is Monster { SummonedBy: null })
        {
            this._dangerZones.RegisterDeath(map.Definition, this._player.Position);
        }
    }

    /// <summary>
    /// Picks the legally warpable map with the strongest monsters the bot can safely fight among those
    /// EASIER than where it stands now - one step down, not a retreat to the starter map. Used when the
    /// current map pays nothing (see <see cref="BarrenMapDuration"/>): the bot keeps stepping down every
    /// barren stretch until it finds ground it can actually farm, and the regular map choice carries it
    /// back up as its gear and level recover.
    /// </summary>
    /// <param name="warp">The warp info of the chosen map.</param>
    /// <param name="mapDefinition">The chosen map.</param>
    /// <param name="monsterLevel">The level of the strongest safe monster there.</param>
    private bool TryPickEasierMap(out WarpInfo warp, out GameMapDefinition mapDefinition, out int monsterLevel)
    {
        warp = default!;
        mapDefinition = default!;
        monsterLevel = 0;

        var current = this._player.CurrentMap?.Definition;

        // With nothing safe here at all, every huntable map is a step down - the comparison must not
        // rule them all out.
        var currentLevel = current is null ? int.MaxValue : this.BestSafeLevel(current, 0, this.GetCurrentZoneFilter());
        if (currentLevel <= 0)
        {
            currentLevel = int.MaxValue;
        }

        foreach (var candidate in this._player.GameContext.Configuration.Maps)
        {
            // The whole map is an upper bound for every part of it, and cheap to judge.
            var mapBest = this.BestSafeLevel(candidate);
            if (mapBest <= 0
                || mapBest <= monsterLevel
                || candidate.TryGetRequirementError(this._player, out _)
                || !BotWarpPlanner.HasWalkableSpawnGate(candidate))
            {
                continue;
            }

            foreach (var (candidateWarp, best) in this.GetHuntingWarps(candidate, 0))
            {
                if (BotMapChoicePlanner.IsBetterStepDown(best, currentLevel, monsterLevel) && this.CanAffordWarp(candidateWarp))
                {
                    warp = candidateWarp;
                    mapDefinition = candidate;
                    monsterLevel = best;
                }
            }
        }

        return mapDefinition is not null;
    }

    /// <summary>
    /// Picks the legally warpable map (other than the current one, or another part of it, see
    /// <see cref="GetHuntingWarps"/>) that offers the strongest monsters the bot can still safely handle
    /// if it is meaningfully better than where the bot stands, with its warp gate.
    /// In escape mode the target only has to reach <paramref name="escapeFloor"/>; that floor is what
    /// keeps a bot from escaping into ground no better than the ground it leaves (see
    /// <see cref="MapFit.EscapeTargetFloor"/>).
    /// </summary>
    /// <param name="escape">Whether the bot has to leave its current map regardless of its strength.</param>
    /// <param name="escapeFloor">The lowest monster level an escape target has to offer.</param>
    /// <param name="warp">The chosen warp.</param>
    /// <param name="mapDefinition">The chosen map.</param>
    /// <param name="monsterLevel">The level of the strongest safe monster there.</param>
    /// <returns>True, if a target was found.</returns>
    private bool TryPickBetterMap(bool escape, int escapeFloor, out WarpInfo warp, out GameMapDefinition mapDefinition, out int monsterLevel)
    {
        // A mastered bot earns nothing below the master-experience floor, so it first looks for a map
        // which pays at all. Only when no map within its reach offers monsters above the floor that it
        // can safely fight does it fall back to hunting by safety alone: standing on a map it cannot
        // survive would not earn it master experience either, and its gear still improves meanwhile.
        var floor = this.MasterExperienceFloor();
        return (floor > 0 && this.TryPickBetterMapCore(escape, floor, escapeFloor, out warp, out mapDefinition, out monsterLevel))
               || this.TryPickBetterMapCore(escape, 0, escapeFloor, out warp, out mapDefinition, out monsterLevel);
    }

    /// <summary>
    /// Picks the best map (see <see cref="TryPickBetterMap"/>), counting only monsters of at least
    /// <paramref name="minimumMonsterLevel"/>.
    /// </summary>
    /// <param name="escape">Whether the bot has to leave its current map.</param>
    /// <param name="minimumMonsterLevel">The lowest monster level worth counting at all.</param>
    /// <param name="escapeFloor">The lowest monster level an escape target has to offer.</param>
    /// <param name="warp">The chosen warp.</param>
    /// <param name="mapDefinition">The chosen map.</param>
    /// <param name="monsterLevel">The level of the strongest safe monster there.</param>
    /// <returns>True, if a target was found.</returns>
    private bool TryPickBetterMapCore(bool escape, int minimumMonsterLevel, int escapeFloor, out WarpInfo warp, out GameMapDefinition mapDefinition, out int monsterLevel)
    {
        warp = default!;
        mapDefinition = default!;
        monsterLevel = 0;

        // Always taking the single strongest map emptied the world: the maps of a level band differ by a
        // few monster levels, so one of them is every bot's answer, and the rest stand deserted - hundreds
        // of bots on Vulcanus while Tarkan, Karutan and Kanturu, which their level opens, see nobody. The
        // strongest map still wins most of the picks (see BestCandidateShare); the runners-up share the
        // rest, so the population spreads over the maps a player of that band would choose between.
        var picked = BotMapChoicePlanner.PickStrongest(this.GetBetterMapCandidates(escape, minimumMonsterLevel, escapeFloor));
        if (picked is not { } choice)
        {
            return false;
        }

        warp = choice.Warp;
        mapDefinition = choice.Map;
        monsterLevel = choice.Level;
        return true;
    }

    /// <summary>
    /// Gets the zones of the map which the bot keeps away from (see <see cref="BotDangerZoneTracker"/>).
    /// </summary>
    /// <param name="mapDefinition">The map.</param>
    /// <returns>The zones of the map with the indexes of the avoided ones; <c>null</c> if none is avoided.</returns>
    private (BotMapZones Zones, HashSet<int> Avoided)? GetAvoidedZones(GameMapDefinition mapDefinition)
    {
        this._dangerZones.Update();
        if (this.GetHuntingZones(mapDefinition) is not { } hunting
            || this._dangerZones.GetAvoidedZoneIndexes(hunting.Entries) is not { } avoided)
        {
            return null;
        }

        return (hunting.Zones, avoided);
    }

    /// <summary>
    /// Gets a filter for the spawn areas which belong to zones the bot keeps away from (see
    /// <see cref="BotDangerZoneTracker"/>), so it doesn't walk back into them on a map it can walk through.
    /// </summary>
    /// <param name="mapDefinition">The map.</param>
    /// <returns>The filter, or <c>null</c> if no zone of the map is avoided.</returns>
    private Func<MonsterSpawnArea, bool>? GetAvoidedGroundFilter(GameMapDefinition mapDefinition)
    {
        if (this.GetAvoidedZones(mapDefinition) is not { } avoided)
        {
            return null;
        }

        return area => avoided.Avoided.Contains(avoided.Zones.GetAreaZone(area, BotHuntingGroundSelector.GroundScatterSpan, HuntingRange));
    }

    /// <summary>
    /// What one evaluation tick works with, gathered once up front (see <see cref="PrepareTickAsync"/>).
    /// The tick's branches all judge the same picture then, instead of some of them re-deriving the
    /// safezone or the leader after a branch already changed the player.
    /// </summary>
    /// <param name="InSafezone">Whether the bot stands in the safezone of the map.</param>
    /// <param name="LeaderToFollow">The party leader the bot follows; <c>null</c> when it hunts on its own.</param>
    /// <param name="IsTetheredToLeader">Whether the follower hunts around its leader on the same map.</param>
    /// <param name="IsSavingForFollowFare">Whether the bot keeps its Zen for a warp to its leader, so it may not warp on its own.</param>
    /// <param name="MonstersNearby">The number of monsters nearby which the bot would actually fight.</param>
    private readonly record struct TickContext(
        bool InSafezone,
        Player? LeaderToFollow,
        bool IsTetheredToLeader,
        bool IsSavingForFollowFare,
        int MonstersNearby);

    /// <summary>
    /// A Manhattan distance heuristic (the pathfinder applies its own estimate multiplier), used to turn the
    /// otherwise unguided search into A* for long-distance bot travel.
    /// </summary>
    private sealed class ManhattanTravelHeuristic : IHeuristic
    {
        public int HeuristicEstimateMultiplier { get; set; }

        public int CalculateHeuristicDistance(Point location, Point target)
            => this.HeuristicEstimateMultiplier * (Math.Abs(location.X - target.X) + Math.Abs(location.Y - target.Y));
    }
}
