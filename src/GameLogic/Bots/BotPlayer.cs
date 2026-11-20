// <copyright file="BotPlayer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Threading;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// A connection-less bot player. It reuses the whole offline-player intelligence (combat, buffs,
/// healing, pickup) and adds a <see cref="BotNavigator"/> which makes it roam to level-appropriate
/// hunting grounds instead of standing on a fixed spawn position.
/// </summary>
public sealed class BotPlayer : OfflinePlayer
{
    /// <summary>
    /// How many loot decisions the administration keeps per bot. Enough to see the recent pattern -
    /// what the bot kept, equipped, sold or stored - without keeping a history for the whole session.
    /// </summary>
    private const int MaxRecentLootDecisions = 20;

    private readonly object _recentLootDecisionsLock = new();
    private readonly List<BotLootDecisionInfo> _recentLootDecisions = [];
    private BotNavigator? _navigator;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotPlayer"/> class.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public BotPlayer(IGameContext gameContext)
        : base(gameContext)
    {
        // File the bot's log entries under its own runtime category
        // (MUnique.OpenMU.GameLogic.Bots.BotPlayer), so a single Serilog MinimumLevel.Override can
        // silence or surface bot logging without touching real players, who keep the base Player category.
        this.Logger = new RuntimeCategoryLogger<Player>(gameContext.LoggerFactory, this);
    }

    /// <inheritdoc />
    public override bool RespawnAndContinue => true;

    /// <summary>
    /// Gets or sets a value indicating whether this bot evolved into its master class and still needs
    /// the "relog" which mounts the master attributes (see <see cref="BotManager.RestartBotAsync"/>).
    /// Set from the bot's own tick, where the evolution runs; acted upon by the maintenance pass, which
    /// is the only place allowed to restart a bot (a restart from within the bot's own timer callback
    /// would tear down the very loop it runs in).
    /// </summary>
    public bool AwaitsMasterRestart { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this bot's AI keeps failing and it has to be restarted
    /// (see <see cref="BotHealthPolicy.ConsecutiveFaultsUntilRestart"/>). Acted upon by the maintenance pass,
    /// which is the only place allowed to restart a bot.
    /// </summary>
    public bool AwaitsFaultRestart { get; set; }

    /// <summary>
    /// Gets what this bot is doing right now - its mode, why it is in it, how long it has been there, the
    /// long form with its recent transitions and the event meeting it is on - or <c>null</c> while it isn't
    /// navigating (not in the world). The administration surfaces report it next to the bot.
    /// </summary>
    public BotActivity? Activity
    {
        get
        {
            if (this._navigator is not { } navigator)
            {
                return null;
            }

            var meeting = BotMeetingActivity.From(this.EventMeeting);
            var lastMeeting = this.LastEventMeeting;
            var detail = navigator.States.Describe();
            if (meeting is { } activeMeeting)
            {
                detail += "\n" + activeMeeting.Detail;
            }
            else if (lastMeeting is { } endedMeeting)
            {
                detail += "\n" + endedMeeting.Detail;
            }

            return new BotActivity(
                navigator.States.Current.ToString(),
                navigator.States.CurrentReason,
                navigator.States.CurrentDuration,
                navigator.States.Summarize(),
                detail,
                navigator.States.TotalFailures,
                navigator.States.LastFailure,
                this.Health.Restarts,
                this.Health.QuarantinedUntilUtc,
                meeting,
                lastMeeting);
        }
    }

    /// <summary>
    /// Gets the most recent loot decisions of this bot, newest first - why it kept, equipped, sold or
    /// stored an item. The bot table reads this without debug logging being enabled; the list is kept
    /// in memory and is empty until the bot makes its first decision.
    /// </summary>
    public IReadOnlyList<BotLootDecisionInfo> RecentLootDecisions
    {
        get
        {
            lock (this._recentLootDecisionsLock)
            {
                return this._recentLootDecisions.ToList();
            }
        }
    }

    /// <summary>
    /// Gets or sets the health supervisor of the character this bot drives. It counts the failing ticks, decides
    /// whether a restart is the right answer and quarantines the bot when restarts do not stick - see
    /// <see cref="BotHealthSupervisor"/>. A directly constructed bot gets its own; a bot spawned by the
    /// <see cref="BotManager"/> shares the one of its character, so a bot which breaks itself again right after a
    /// restart is recognized as flapping instead of being restarted in a loop - and another character of the same
    /// account doesn't inherit its quarantine.
    /// </summary>
    internal BotHealthSupervisor Health { get; set; } = new();

    /// <inheritdoc />
    /// <remarks>
    /// A bot opens its store only on a market shift (see <see cref="BotMarketShift"/>): a store which was
    /// open when it logged out must stay closed after the next login, wherever the bot enters the game.
    /// </remarks>
    protected override bool IsPlayerStoreOpeningAfterEnterSupported => false;

    /// <summary>
    /// Forgets the failures, the restarts and the quarantine of this bot's character, so it is allowed to run and to be
    /// restarted again. This is the operator's answer to "I fixed the cause" - the policy itself is unchanged, and a
    /// bot which still fails every tick simply keeps asking for a restart.
    /// </summary>
    public void ResetHealth() => this.Health.Reset();

    /// <inheritdoc />
    public override async ValueTask StopAsync()
    {
        await this.StopNavigatorAsync().ConfigureAwait(false);
        await base.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Records a loot decision for the administration surfaces. Every decision is kept in the small
    /// in-memory ring, independent of whether the debug log samples it, so an operator can inspect the
    /// reason without turning the log up.
    /// </summary>
    /// <param name="item">The item the decision was about.</param>
    /// <param name="decision">The decision and its reason.</param>
    internal void RecordLootDecision(Item item, BotLootDecision decision)
    {
        var info = BotLootDecisionInfo.From(item, decision, DateTimeOffset.UtcNow);
        lock (this._recentLootDecisionsLock)
        {
            this._recentLootDecisions.Insert(0, info);
            if (this._recentLootDecisions.Count > MaxRecentLootDecisions)
            {
                this._recentLootDecisions.RemoveAt(this._recentLootDecisions.Count - 1);
            }
        }
    }

    /// <inheritdoc />
    internal override void OnAiTickSucceeded() => this.Health.RecordTickSucceeded();

    /// <inheritdoc />
    internal override bool OnAiTickFailed(Exception? exception = null)
    {
        if (this.Health.RecordFault(exception, out var shouldLog))
        {
            // The supervisor raises the request exactly once, at the tick which crosses the threshold; the
            // maintenance pass consumes AwaitsFaultRestart and the restart resets the counter.
            this.Logger.LogWarning(
                "Bot '{Name}' failed {Count} AI ticks in a row and asks for a restart to heal it. Last fault: {Fault}",
                this.Name,
                BotHealthPolicy.ConsecutiveFaultsUntilRestart,
                this.Health.LastFault);
            this.AwaitsFaultRestart = true;
        }

        return shouldLog;
    }

    /// <inheritdoc />
    protected override void StartIntelligence()
    {
        base.StartIntelligence();
        this._navigator = new BotNavigator(this);
        this._navigator.Start();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A bot on a market shift settles it before the base class saves it on the way out (see
    /// <see cref="BotMerchandise.EndShiftOnLogoutAsync"/>).
    /// </remarks>
    protected override async ValueTask InternalDisconnectAsync()
    {
        await this.StopNavigatorAsync().ConfigureAwait(false);
        try
        {
            // The MU Helper tick keeps running until the base class stops it; it holds the same lock.
            await this.RunPersistenceExclusiveAsync(() => BotMerchandise.EndShiftOnLogoutAsync(this)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "Bot '{Name}' couldn't end its market shift on logout.", this.Name);
        }

        await base.InternalDisconnectAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await this.StopNavigatorAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private async ValueTask StopNavigatorAsync()
    {
        if (this._navigator is { } navigator)
        {
            this._navigator = null;
            await navigator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
