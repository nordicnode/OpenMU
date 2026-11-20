// <copyright file="BotState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// What a bot does in one navigation tick - the mode whose branch claimed the tick.
/// </summary>
/// <remarks>
/// The bot's modes used to exist only as a chain of <c>if (await TryX(...)) return;</c> calls plus a handful
/// of flags on the player. Naming them makes the priority order auditable, and lets
/// <see cref="BotStateMachine"/> answer "what is this bot doing, since when, and why" (see
/// <see cref="BotStateMachine.Describe"/>) instead of leaving it to whoever reads the loop.
/// </remarks>
internal enum BotState
{
    /// <summary>
    /// Nothing claimed the tick: a walk is still in progress, or the bot waits out a short grace.
    /// </summary>
    Idle,

    /// <summary>
    /// Fighting the monsters around it.
    /// </summary>
    Hunt,

    /// <summary>
    /// Walking somewhere that is not a fight: a hunting ground, a warp gate, out of town.
    /// </summary>
    Travel,

    /// <summary>
    /// Following the party leader, on this map or to his map.
    /// </summary>
    FollowLeader,

    /// <summary>
    /// Waiting at, or walking to, the meeting point of an announced event.
    /// </summary>
    EventMeeting,

    /// <summary>
    /// Travelling to an invasion and fighting its champions.
    /// </summary>
    BossAttempt,

    /// <summary>
    /// A market shift: travelling to the market, walking to the stall, or trading there.
    /// </summary>
    MarketShift,

    /// <summary>
    /// A town errand: selling junk loot and restocking potions.
    /// </summary>
    Shopping,

    /// <summary>
    /// A town errand: claiming the buff of a dialog NPC.
    /// </summary>
    Buff,

    /// <summary>
    /// A town errand: mixing an item at the chaos machine.
    /// </summary>
    ChaosMachine,

    /// <summary>
    /// Working on the quest which unlocks the combo: visiting the giver, farming its monsters or
    /// handing it in.
    /// </summary>
    Quest,

    /// <summary>
    /// Marching back to the place where another player killed it.
    /// </summary>
    Revenge,

    /// <summary>
    /// Inside a mini-game event, where the event dictates the pace and the normal routine is suspended.
    /// </summary>
    MiniGame,

    /// <summary>
    /// Resetting the character to start the next cycle.
    /// </summary>
    Reset,

    /// <summary>
    /// Breaking free from a position the bot was wedged on.
    /// </summary>
    Recover,
}
