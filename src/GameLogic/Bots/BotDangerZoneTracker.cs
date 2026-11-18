// <copyright file="BotDangerZoneTracker.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Collections.Concurrent;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Remembers where a bot died to monsters and keeps it away from such places for a while.
/// </summary>
/// <remarks>
/// The safety check judges one monster at a time, so a zone of dense packs (the top floors of the Lost
/// Tower) can pass it and still kill the bot, which then respawned, warped back and died again in a loop.
/// After <see cref="MonsterDeathThreshold"/> deaths within <see cref="MonsterDeathWindow"/> in the zone of
/// one warp entry, that zone is avoided for <see cref="DangerousZoneAvoidance"/>, so the bot settles for
/// the next best zone instead.
/// </remarks>
internal sealed class BotDangerZoneTracker
{
    /// <summary>How long deaths by monsters count toward <see cref="MonsterDeathThreshold"/>.</summary>
    private static readonly TimeSpan MonsterDeathWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the bot keeps away from a zone it died in too often (see <see cref="MonsterDeathThreshold"/>).</summary>
    private static readonly TimeSpan DangerousZoneAvoidance = TimeSpan.FromMinutes(30);

    /// <summary>
    /// After this many deaths by monsters in the zone of one warp entry within <see cref="MonsterDeathWindow"/>,
    /// the bot keeps away from that zone for <see cref="DangerousZoneAvoidance"/>.
    /// </summary>
    private static readonly int MonsterDeathThreshold = 3;

    private readonly Player _player;
    private readonly TimeProvider _timeProvider;
    private readonly Func<GameMapDefinition, (BotMapZones Zones, IReadOnlyList<WarpInfo> Entries)?> _getHuntingZones;

    /// <summary>
    /// Deaths by monsters, noted by the death event (another thread) and evaluated in the navigation
    /// ticks (see <see cref="Update"/>).
    /// </summary>
    private readonly ConcurrentQueue<(GameMapDefinition Map, Point Position, DateTimeOffset At)> _monsterDeaths = new();

    /// <summary>The recent deaths by monsters with the warp entry whose zone they happened in.</summary>
    private readonly List<(WarpInfo Entry, DateTimeOffset At)> _recentZoneDeaths = [];

    /// <summary>The warp entries whose zones the bot keeps away from, with the end of the avoidance.</summary>
    private readonly Dictionary<WarpInfo, DateTimeOffset> _avoidedEntries = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Initializes a new instance of the <see cref="BotDangerZoneTracker"/> class.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="getHuntingZones">Resolves the zones and warp entries of a map the bot may use.</param>
    internal BotDangerZoneTracker(
        Player player,
        TimeProvider timeProvider,
        Func<GameMapDefinition, (BotMapZones Zones, IReadOnlyList<WarpInfo> Entries)?> getHuntingZones)
    {
        this._player = player;
        this._timeProvider = timeProvider;
        this._getHuntingZones = getHuntingZones;
    }

    /// <summary>
    /// Gets a value indicating whether the bot currently keeps away from at least one zone.
    /// </summary>
    internal bool HasAvoidedEntries => this._avoidedEntries.Count > 0;

    /// <summary>
    /// Notes a death by a monster for <see cref="MonsterDeathThreshold"/>.
    /// </summary>
    /// <param name="map">The map of the death.</param>
    /// <param name="position">The position of the death.</param>
    internal void RegisterDeath(GameMapDefinition map, Point position)
        => this._monsterDeaths.Enqueue((map, position, this._timeProvider.GetUtcNow()));

    /// <summary>
    /// Evaluates the deaths by monsters noted since the last call and starts the avoidance of a zone the
    /// bot died in too often.
    /// </summary>
    internal void Update()
    {
        var now = this._timeProvider.GetUtcNow();
        while (this._monsterDeaths.TryDequeue(out var death))
        {
            if (this._getHuntingZones(death.Map) is not { } hunting
                || hunting.Zones.GetZone(death.Position) is not (var zone and >= 0))
            {
                continue;
            }

            var entry = hunting.Entries[zone];
            if (this.IsAvoided(entry))
            {
                // Dying on the way out of an avoided zone doesn't make it any more avoided.
                continue;
            }

            this._recentZoneDeaths.RemoveAll(d => now - d.At > MonsterDeathWindow);
            this._recentZoneDeaths.Add((entry, death.At));
            if (this._recentZoneDeaths.Count(d => ReferenceEquals(d.Entry, entry)) >= MonsterDeathThreshold)
            {
                this._recentZoneDeaths.RemoveAll(d => ReferenceEquals(d.Entry, entry));
                this._avoidedEntries[entry] = now + DangerousZoneAvoidance;
                this._player.Logger.LogInformation(
                    "Bot {Character} died {Count} times near warp {Warp} on map {Map}; it keeps away from there for a while.",
                    this._player.Name,
                    MonsterDeathThreshold,
                    entry,
                    death.Map.Name);
            }
        }
    }

    /// <summary>
    /// Determines whether the bot keeps away from the zone of the warp entry.
    /// </summary>
    /// <param name="entry">The warp entry.</param>
    /// <returns><c>true</c>, if the zone is avoided right now.</returns>
    internal bool IsAvoided(WarpInfo entry)
    {
        if (!this._avoidedEntries.TryGetValue(entry, out var until))
        {
            return false;
        }

        if (this._timeProvider.GetUtcNow() < until)
        {
            return true;
        }

        this._avoidedEntries.Remove(entry);
        return false;
    }

    /// <summary>
    /// Gets the indexes of the entries whose zones the bot keeps away from.
    /// </summary>
    /// <param name="entries">The warp entries of the map, in the order of the zones.</param>
    /// <returns>The indexes of the avoided zones; <c>null</c> if none is avoided.</returns>
    internal HashSet<int>? GetAvoidedZoneIndexes(IReadOnlyList<WarpInfo> entries)
    {
        if (this._avoidedEntries.Count == 0)
        {
            return null;
        }

        var avoided = Enumerable.Range(0, entries.Count).Where(i => this.IsAvoided(entries[i])).ToHashSet();
        return avoided.Count == 0 ? null : avoided;
    }
}
