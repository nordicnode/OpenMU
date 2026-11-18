// <copyright file="BotFlappingAlarm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Globalization;

/// <summary>
/// Finds the bots which keep breaking themselves: the ones which were restarted again and again, and the ones
/// which are held back from another restart right now.
/// </summary>
/// <remarks>
/// A bot whose ticks keep failing is restarted, because a fresh login rebuilds the attribute graph a lost race
/// may have corrupted (see the health policy of the game logic). A restart which does not stick is a story an
/// operator has to hear: the bot is in the world, looks busy and plays badly, or is not restarted at all while a
/// quarantine runs. Without this alarm that only shows up as one line in the log among thousands.
/// </remarks>
public static class BotFlappingAlarm
{
    /// <summary>
    /// How many restarts make a bot a flapping bot. It matches the number of restarts after which the game logic
    /// stops restarting and quarantines instead, so the alarm starts exactly when the server starts backing off.
    /// </summary>
    public const int RestartsThreshold = 3;

    /// <summary>
    /// How many accounts the sentence names before it only counts the rest.
    /// </summary>
    public const int NamesInSentence = 5;

    /// <summary>
    /// Finds the bots which keep failing, the one with the longest quarantine still to run first.
    /// </summary>
    /// <param name="bots">What each bot reports about its restarts and its quarantine.</param>
    /// <returns>One entry per flapping bot; empty if every bot is healthy or merely slow.</returns>
    public static IReadOnlyList<FlappingBot> Find(IEnumerable<FlappingBot> bots)
    {
        return bots
            .Where(bot => bot.Restarts >= RestartsThreshold || bot.QuarantinedSeconds > 0)
            .OrderByDescending(bot => bot.QuarantinedSeconds)
            .ThenByDescending(bot => bot.Restarts)
            .ThenBy(bot => bot.LoginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Puts the alarm into the one sentence a summary or a banner shows; empty while no bot keeps failing.
    /// </summary>
    /// <param name="bots">The flapping bots, as <see cref="Find"/> returned them.</param>
    /// <param name="totalBots">How many bots were examined, so the sentence can say how large the problem is.</param>
    /// <returns>The sentence, or an empty string.</returns>
    public static string Describe(IReadOnlyList<FlappingBot> bots, int totalBots)
    {
        if (bots.Count == 0)
        {
            return string.Empty;
        }

        var named = string.Join(", ", bots.Take(NamesInSentence).Select(DescribeBot));
        if (bots.Count > NamesInSentence)
        {
            named += string.Format(CultureInfo.InvariantCulture, ", and {0} more", bots.Count - NamesInSentence);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} of {1} bots {2} failing: {3}.",
            bots.Count,
            totalBots,
            bots.Count == 1 ? "keeps" : "keep",
            named);
    }

    private static string DescribeBot(FlappingBot bot)
    {
        var facts = new List<string>();
        if (bot.Restarts > 0)
        {
            facts.Add(string.Format(CultureInfo.InvariantCulture, "{0} restarts", bot.Restarts));
        }

        if (bot.QuarantinedSeconds > 0)
        {
            // Rounded up, so a bot which is held back never reads as "quarantined for another 0 minutes".
            var minutes = Math.Max(1, (int)Math.Ceiling(bot.QuarantinedSeconds / 60));
            facts.Add(string.Format(CultureInfo.InvariantCulture, "quarantined for another {0} minutes", minutes));
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", bot.LoginName ?? bot.CharacterName, string.Join(", ", facts));
    }

    /// <summary>
    /// One bot which keeps failing.
    /// </summary>
    /// <param name="LoginName">The account the bot drives, which is what an operator acts on.</param>
    /// <param name="CharacterName">The character the bot drives.</param>
    /// <param name="Restarts">How many times the account was restarted to heal it.</param>
    /// <param name="QuarantinedSeconds">How many seconds the bot is still held back from another restart.</param>
    public readonly record struct FlappingBot(string? LoginName, string? CharacterName, int Restarts, double QuarantinedSeconds);
}
