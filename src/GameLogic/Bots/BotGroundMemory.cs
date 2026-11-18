// <copyright file="BotGroundMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Collections.Concurrent;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// The kinds of bad experience a bot remembers about a hunting ground.
/// </summary>
internal enum GroundMemoryReason
{
    /// <summary>The bot got wedged on its way to, or at, the ground.</summary>
    Stuck,

    /// <summary>A monster killed the bot at the ground.</summary>
    Died,
}

/// <summary>
/// Remembers the spots where a bot recently got stuck or died, so the next hunting ground it picks is a
/// different one. Short-term and per bot: the memory lives for minutes, not across sessions, and only
/// steers the ground choice - it never makes the bot stop hunting.
/// </summary>
/// <remarks>
/// The ground pick already knows the level band, the death sites caused by players and the spots other
/// bots have claimed, but nothing about its own recent failures at a particular place: a bot which got
/// wedged at a spawn walked back to it after the next reposition, and one which died to a dense pack
/// picked the same pack again. The entries decay, and they are never a hard block - a remembered ground
/// stays a heavily discounted candidate, so a map with nothing else still gets hunted.
/// Entries are reported from the death event (another thread), so they are queued and folded in by the
/// navigation tick, like the zone tracker does.
/// </remarks>
internal sealed class BotGroundMemory
{
    /// <summary>Radius (tiles) around a remembered wedge in which grounds are discounted.</summary>
    internal const int StuckRange = 10;

    /// <summary>Radius (tiles) around a remembered death in which grounds are discounted.</summary>
    internal const int DeathRange = 20;

    /// <summary>How long a wedge is remembered.</summary>
    internal static readonly TimeSpan StuckLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a death by a monster is remembered.</summary>
    internal static readonly TimeSpan DeathLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How much a remembered death discounts a ground at its very center.</summary>
    private const float DeathSeverity = 1f;

    /// <summary>How much a remembered wedge discounts a ground at its very center.</summary>
    private const float StuckSeverity = 0.7f;

    /// <summary>The weight a fully remembered ground keeps, so it is never impossible to pick.</summary>
    private const float MinimumWeightShare = 0.1f;

    /// <summary>How many entries are kept; the oldest fall out first.</summary>
    private const int MaxEntries = 24;

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentQueue<(GameMapDefinition Map, Point Position, GroundMemoryReason Reason, DateTimeOffset At)> _reported = new();
    private readonly List<(GameMapDefinition Map, Point Position, GroundMemoryReason Reason, DateTimeOffset At, DateTimeOffset Until)> _spots = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="BotGroundMemory"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider.</param>
    internal BotGroundMemory(TimeProvider timeProvider)
    {
        this._timeProvider = timeProvider;
    }

    /// <summary>
    /// Notes that the bot got stuck or died at a spot. Safe to call from another thread.
    /// </summary>
    /// <param name="map">The map of the spot.</param>
    /// <param name="position">The position of the spot.</param>
    /// <param name="reason">What happened there.</param>
    internal void Remember(GameMapDefinition map, Point position, GroundMemoryReason reason)
        => this._reported.Enqueue((map, position, reason, this._timeProvider.GetUtcNow()));

    /// <summary>
    /// Folds the reported spots in and drops the expired ones. Called once per navigation tick.
    /// </summary>
    internal void Update()
    {
        var now = this._timeProvider.GetUtcNow();
        while (this._reported.TryDequeue(out var spot))
        {
            this._spots.Add((spot.Map, spot.Position, spot.Reason, spot.At, spot.At + LifetimeOf(spot.Reason)));
        }

        this._spots.RemoveAll(spot => now >= spot.Until);
        if (this._spots.Count > MaxEntries)
        {
            this._spots.RemoveRange(0, this._spots.Count - MaxEntries);
        }
    }

    /// <summary>
    /// Gets how attractive a ground still is: 1 for an unremembered one, and down to
    /// <see cref="MinimumWeightShare"/> for one the bot has just failed at. The value decays with the age
    /// of the entry and with the distance to the remembered spot.
    /// </summary>
    /// <param name="map">The map of the ground.</param>
    /// <param name="position">The center of the ground.</param>
    /// <returns>The weight factor in the range of <see cref="MinimumWeightShare"/> to 1.</returns>
    internal float GetWeightFactor(GameMapDefinition map, Point position)
    {
        var now = this._timeProvider.GetUtcNow();
        var worst = 0f;
        foreach (var spot in this._spots)
        {
            if (!object.Equals(map, spot.Map) || now >= spot.Until)
            {
                continue;
            }

            var range = RangeOf(spot.Reason);
            var distance = Math.Abs(spot.Position.X - position.X) + Math.Abs(spot.Position.Y - position.Y);
            if (distance > range)
            {
                continue;
            }

            var nearness = 1f - (distance / (float)range);
            var freshness = (float)((spot.Until - now).TotalMilliseconds / (spot.Until - spot.At).TotalMilliseconds);
            var penalty = SeverityOf(spot.Reason) * Math.Clamp(freshness, 0f, 1f) * nearness;
            worst = Math.Max(worst, penalty);
        }

        return 1f - (worst * (1f - MinimumWeightShare));
    }

    /// <summary>
    /// Determines whether the ground is one the bot recently failed at, so it is not picked while other
    /// grounds are available.
    /// </summary>
    /// <param name="map">The map of the ground.</param>
    /// <param name="position">The center of the ground.</param>
    /// <returns><c>true</c>, if the ground should be avoided right now.</returns>
    internal bool IsAvoided(GameMapDefinition map, Point position)
        => this.GetWeightFactor(map, position) < 0.5f;

    private static TimeSpan LifetimeOf(GroundMemoryReason reason)
        => reason == GroundMemoryReason.Died ? DeathLifetime : StuckLifetime;

    private static int RangeOf(GroundMemoryReason reason)
        => reason == GroundMemoryReason.Died ? DeathRange : StuckRange;

    private static float SeverityOf(GroundMemoryReason reason)
        => reason == GroundMemoryReason.Died ? DeathSeverity : StuckSeverity;
}
