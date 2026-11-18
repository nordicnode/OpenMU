// <copyright file="BotModes.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The mode names the administration surfaces report, and which of them a bot leaves quickly by design.
/// </summary>
/// <remarks>
/// The web layers carry the mode of a bot as a string, because that is what they put into their reports and
/// into their JSON. These names are tied to <see cref="BotState"/> with <c>nameof</c>, so a renamed mode
/// breaks the build instead of silently dropping out of the stuck-bot rule.
/// </remarks>
public static class BotModes
{
    private static readonly HashSet<string> WaitingModes = new(StringComparer.Ordinal)
    {
        nameof(BotState.Idle),
        nameof(BotState.Travel),
        nameof(BotState.Recover),
        nameof(BotState.Shopping),
        nameof(BotState.Buff),
        nameof(BotState.ChaosMachine),
    };

    /// <summary>
    /// Determines whether the mode is one a bot leaves within a tick or two: it is tied up in nothing more than
    /// a walk, a town errand or breaking free, and none of those take long.
    /// </summary>
    /// <remarks>
    /// The other modes are expected to last: hunting, following a leader, a market shift, a revenge march,
    /// waiting for an announced event and a mini-game all keep a bot busy for as long as they take, so a long
    /// stay in one of them says nothing about whether the bot is stuck.
    /// </remarks>
    /// <param name="mode">The mode name, as reported by <see cref="BotActivity.State"/>.</param>
    /// <returns><c>true</c>, if a long stay in the mode means the bot is stuck.</returns>
    public static bool IsWaiting(string? mode) => mode is not null && WaitingModes.Contains(mode);
}
