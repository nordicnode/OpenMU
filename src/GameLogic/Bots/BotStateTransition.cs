// <copyright file="BotStateTransition.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// One transition of a bot from one <see cref="BotState"/> to the next, with the reason the tick claiming
/// it gave.
/// </summary>
/// <param name="From">The state the bot left.</param>
/// <param name="To">The state the bot entered.</param>
/// <param name="AtUtc">The time of the transition.</param>
/// <param name="Reason">Why the tick changed the state.</param>
internal readonly record struct BotStateTransition(BotState From, BotState To, DateTimeOffset AtUtc, string Reason);

/// <summary>
/// Formats a <see cref="BotStateTransition"/> for the log and the state command.
/// </summary>
internal static class BotStateTransitionFormat
{
    /// <summary>
    /// Formats the transition as <c>Hunt -&gt; Travel (walking to the ground)</c>, followed by the time.
    /// </summary>
    /// <param name="transition">The transition.</param>
    /// <returns>The formatted transition.</returns>
    internal static string Format(BotStateTransition transition)
        => $"{transition.From}->{transition.To} {transition.AtUtc:HH:mm:ss} ({transition.Reason})";
}
