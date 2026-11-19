// <copyright file="BotEventMeeting.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Where and until when a bot waits for players after it announced an opened event, or after a
/// player whose party search it answered named the meeting place (see <see cref="BotChatPlugIn"/>).
/// A bot which posts "Blood Castle open, anyone going?" and keeps hunting in another corner of the
/// world could never be found - so it goes to the meeting point and stays there until the entrance
/// closes, ready to be invited into a party.
/// </summary>
/// <param name="Map">The map of the meeting point.</param>
/// <param name="NpcPosition">
/// The position next to which the bot waits: the event's entry NPC (Messenger of Archangel, Charon),
/// or the coordinates the player named; <c>null</c> to wait anywhere in the map's safezone - Chaos
/// Castle is entered with the ticket alone, it has no entry NPC.
/// </param>
/// <param name="UntilUtc">When the bot stops waiting, e.g. when the event's entrance closes.</param>
/// <param name="Gate">
/// The gate through which the bot enters the map: the one of the warp the player named ("devias3"), or
/// the one closest to the named coordinates - on a map like the Lost Tower, whose floors aren't connected
/// by walkable ground, the only way to get there; <c>null</c> for a spawn gate of the map.
/// </param>
/// <param name="Event">
/// The mini game whose entrance opened, when the meeting was created by the event announcement (see
/// <see cref="BotChatEventAnnouncer"/>). It carries the entry rules which make the waiting bots a
/// goal-driven event party; <c>null</c> when a player named the place.
/// </param>
/// <remarks>
/// <see cref="State"/> is the meeting's intent, maintained by <see cref="BotNavigator"/>: it tells
/// whether the bot is still on its way, already waits at the place, or why it gave up. The state is
/// runtime only and deliberately not part of the record's equality, so a meeting doesn't become a
/// different meeting just because the bot set off.
/// </remarks>
internal sealed record BotEventMeeting(GameMapDefinition Map, Point? NpcPosition, DateTime UntilUtc, ExitGate? Gate = null, MiniGameDefinition? Event = null)
{
    /// <summary>
    /// Gets the state of the meeting, see <see cref="BotMeetingState"/>.
    /// </summary>
    internal BotMeetingState State { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the meeting is still going on - announced, traveling or waiting.
    /// </summary>
    internal bool IsActive => this.State is BotMeetingState.Announced or BotMeetingState.Traveling or BotMeetingState.Waiting;

    /// <summary>
    /// Determines whether the meeting's time ran out at the given time. The time is the event's entrance
    /// closing, or the party meeting wait time the player agreed to.
    /// </summary>
    /// <param name="utcNow">The current time.</param>
    /// <returns><c>true</c>, if the meeting must not be waited for anymore.</returns>
    internal bool IsTimedOut(DateTime utcNow) => utcNow >= this.UntilUtc;

    /// <summary>
    /// Moves the meeting to the next state, unless it already is there or has ended.
    /// </summary>
    /// <param name="state">The state to move to.</param>
    /// <returns><c>true</c>, if the state changed - the caller logs the transition then.</returns>
    internal bool AdvanceTo(BotMeetingState state)
    {
        if (!this.IsActive || this.State == state)
        {
            return false;
        }

        this.State = state;
        return true;
    }

    /// <summary>
    /// Gets the meeting as a fallback rendezvous: the bot waits in the safezone of the meeting's map
    /// instead of next to its entry NPC, which turned out to be unreachable (see <see cref="BotNavigator"/>).
    /// The map - and with it where players have to look - stays the same.
    /// </summary>
    /// <returns>The meeting without the unreachable NPC point and its gate, in the same state.</returns>
    internal BotEventMeeting ToSafezoneFallback()
        => this with { NpcPosition = null, Gate = null, State = this.State };
}
