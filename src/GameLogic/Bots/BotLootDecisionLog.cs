// <copyright file="BotLootDecisionLog.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Records the loot decisions of bots for the administration surfaces and writes a sample of them to
/// the debug log, so an operator can follow why a bot kept, equipped, sold or stored an item without
/// logging every decision.
/// </summary>
internal static class BotLootDecisionLog
{
    /// <summary>
    /// Records the decision on the bot and logs it at the configured sample rate. Recording is independent
    /// of the sample, so the bot table stays useful while debug logging is off. Logging is off when the bot
    /// feature has no configuration, which is the case in unit tests and for bots outside a configured server.
    /// </summary>
    /// <param name="player">The bot.</param>
    /// <param name="item">The item the decision is about.</param>
    /// <param name="decision">The decision and its reason.</param>
    /// <param name="context">An optional short context, e.g. <c>pickup</c> or <c>merchant trip</c>.</param>
    internal static void Log(Player player, Item item, BotLootDecision decision, string? context = null)
    {
        if (player is BotPlayer bot)
        {
            bot.RecordLootDecision(item, decision);
        }

        var configuration = BotFeaturePlugIn.GetConfiguration(player.GameContext);
        var samplePercent = configuration?.GetEffectiveLootDecisionLogSamplePercent() ?? 0;
        if (samplePercent <= 0 || !Rand.NextRandomBool(samplePercent))
        {
            return;
        }

        player.Logger.LogDebug(
            "Bot '{Name}' loot decision: '{Item}' -> {Decision}{Context}.",
            player.Name,
            item,
            decision,
            context is null ? string.Empty : $" ({context})");
    }
}
