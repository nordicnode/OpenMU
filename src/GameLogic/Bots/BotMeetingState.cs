// <copyright file="BotMeetingState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// How far a bot got with a meeting it announced (see <see cref="BotEventMeeting"/>). Runtime state
/// only - it is never persisted, and a bot which ends a meeting starts the next one as
/// <see cref="Announced"/> again.
/// </summary>
/// <remarks>
/// The three active states tell a player where to look for the bot; the three terminal states tell
/// why the meeting is over. A terminal state can't be left, which keeps a navigator tick and a chat
/// answer from disagreeing about a meeting which just ended.
/// </remarks>
internal enum BotMeetingState
{
    /// <summary>
    /// The bot announced the meeting, but hasn't set off yet. This is where a meeting starts.
    /// </summary>
    Announced,

    /// <summary>
    /// The bot is on its way to the meeting place.
    /// </summary>
    Traveling,

    /// <summary>
    /// The bot arrived at the meeting place and waits there for the players.
    /// </summary>
    Waiting,

    /// <summary>
    /// The bot was taken into a party, or the event started: the meeting served its purpose.
    /// </summary>
    Joined,

    /// <summary>
    /// The meeting time ran out before a player came, e.g. because the event's entrance closed.
    /// </summary>
    Expired,

    /// <summary>
    /// The bot gave up because it couldn't reach the meeting place - no route, no gate or no landing.
    /// </summary>
    Abandoned,
}
