// <copyright file="BotStuckAlarm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Globalization;
using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Finds the bots which have been waiting for something for so long that the wait itself is the story - the
/// bot is stuck, and no tick is failing to say so.
/// </summary>
/// <remarks>
/// The rule reads two facts per bot, the mode it is in and the time it has already spent there, and only counts
/// the modes a bot leaves quickly by design (see <see cref="BotModes.IsWaiting(string?)"/>). A slow bot is
/// therefor told apart from a busy one: twelve bots hunting for an hour are not an alarm, one bot waiting in
/// town for an hour is.
/// </remarks>
public static class BotStuckAlarm
{
    /// <summary>How long a bot may wait in one of the short-lived modes before it counts as stuck.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Groups the stuck bots by the mode they wait in, the mode which holds the most of them first.
    /// </summary>
    /// <param name="bots">The mode and the seconds each bot has already spent in it.</param>
    /// <param name="threshold">How long a bot has to wait before it counts as stuck.</param>
    /// <returns>One entry per mode which holds stuck bots; empty if no bot is stuck.</returns>
    public static IReadOnlyList<StuckMode> Find(IEnumerable<(string? State, double SecondsInState)> bots, TimeSpan threshold)
    {
        return bots
            .Where(bot => BotModes.IsWaiting(bot.State) && bot.SecondsInState >= threshold.TotalSeconds)
            .GroupBy(bot => bot.State!, StringComparer.Ordinal)
            .Select(group => new StuckMode(group.Key, group.Count(), group.Max(bot => bot.SecondsInState)))
            .OrderByDescending(mode => mode.Count)
            .ThenByDescending(mode => mode.LongestSeconds)
            .ThenBy(mode => mode.State, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Puts the alarm into the one sentence a summary or a banner shows; empty while no bot is stuck.
    /// </summary>
    /// <param name="modes">The stuck modes, as <see cref="Find"/> returned them.</param>
    /// <param name="totalBots">How many bots were examined, so the sentence can say how large the problem is.</param>
    /// <param name="threshold">How long a bot has to wait before it counts as stuck.</param>
    /// <returns>The sentence, or an empty string.</returns>
    public static string Describe(IReadOnlyList<StuckMode> modes, int totalBots, TimeSpan threshold)
    {
        if (modes.Count == 0)
        {
            return string.Empty;
        }

        var stuck = modes.Sum(mode => mode.Count);
        var perMode = string.Join(", ", modes.Select(mode => string.Format(CultureInfo.InvariantCulture, "{0} {1}", mode.State, mode.Count)));
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} of {1} bots wait for more than {2} minutes: {3}.",
            stuck,
            totalBots,
            (int)threshold.TotalMinutes,
            perMode);
    }

    /// <summary>
    /// One mode which holds stuck bots.
    /// </summary>
    /// <param name="State">The mode the bots wait in.</param>
    /// <param name="Count">How many bots wait in it.</param>
    /// <param name="LongestSeconds">How long the bot which waited longest has been in it.</param>
    public readonly record struct StuckMode(string State, int Count, double LongestSeconds);
}
