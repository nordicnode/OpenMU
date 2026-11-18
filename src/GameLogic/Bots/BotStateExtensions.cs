// <copyright file="BotStateExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Classifies the <see cref="BotState"/> values into the groups the bot's priority order works with.
/// </summary>
internal static class BotStateExtensions
{
    /// <summary>
    /// Gets a value indicating whether the state is a side trip the bot finishes before it returns to its
    /// routine: a town errand, a market shift, an announced event meeting or a revenge march. While one of
    /// these runs, the errand's own flags on the player are still set - which is the invariant
    /// <see cref="BotStateMachine.GuardViolations"/> watches over.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns><c>true</c>, if the state is an errand.</returns>
    internal static bool IsErrand(this BotState state)
        => state is BotState.Shopping
            or BotState.Buff
            or BotState.ChaosMachine
            or BotState.MarketShift
            or BotState.EventMeeting
            or BotState.Revenge;

    /// <summary>
    /// Gets a value indicating whether the state suspends the whole routine instead of continuing it: the
    /// pace of a mini-game event or the reset which ends the current cycle.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns><c>true</c>, if the state suspends the routine.</returns>
    internal static bool IsSuspended(this BotState state)
        => state is BotState.MiniGame or BotState.Reset;
}
