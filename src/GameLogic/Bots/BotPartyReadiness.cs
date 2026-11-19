// <copyright file="BotPartyReadiness.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Answers what a bot can contribute to a goal-driven party and whether it is ready for one.
/// Pure decision logic, so the rules can be tested without a running world.
/// </summary>
internal static class BotPartyReadiness
{
    /// <summary>
    /// Gets the role a bot plays in a party, derived from its class and build.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The role.</returns>
    internal static BotPartyRole GetRole(Player bot)
    {
        if (BotBuild.IsSupportElf(bot))
        {
            return BotPartyRole.Support;
        }

        var line = BotBuild.GetBuildKey(bot).Line;
        return line is BotClassNumbers.DarkKnightNumber
            or BotClassNumbers.MagicGladiatorNumber
            or BotClassNumbers.DarkLordNumber
            or BotClassNumbers.RageFighterNumber
            ? BotPartyRole.Vanguard
            : BotPartyRole.Damage;
    }

    /// <summary>
    /// Gets a rough gear score of a bot: the quality sum of its equipped pieces (see
    /// <see cref="BotEquipmentHandler.GetItemScore"/>). It grows with the bot's level because better
    /// gear drops and is worn as the bot progresses, which is what the readiness checks compare against.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The gear score; 0 for a bot without equipment.</returns>
    internal static int GetGearScore(Player bot)
        => bot.SelectedCharacter?.Inventory?.Items
               .Where(item => item.ItemSlot < InventoryConstants.EquippableSlotsCount)
               .Sum(BotEquipmentHandler.GetItemScore) ?? 0;

    /// <summary>
    /// Gets the effective level which readiness checks use.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The effective level.</returns>
    internal static int GetLevel(OfflinePlayer bot) => BotResetHandler.GetEffectiveLevel(bot);

    /// <summary>
    /// Determines whether a bot is free to join a goal-driven party now.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="goal">The goal of the party.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c>, if the bot is alive, in the world and not busy with an errand or a grudge.</returns>
    internal static bool IsAvailable(OfflinePlayer bot, BotPartyGoal goal, DateTime now)
    {
        if (bot.Account?.IsBot != true
            || bot.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !bot.IsAlive
            || bot.Attributes is null
            || bot.CurrentMiniGame is not null
            || bot.IsOnShoppingTrip
            || bot.IsOnBuffTrip
            || bot.IsOnChaosMachineTrip
            || bot.HasRevengeIntent)
        {
            return false;
        }

        // A merchant is available through its running shift; a guard through being free of one. Any
        // other goal belongs to a bot which is not behind a store.
        if (goal == BotPartyGoal.MarketEscort)
        {
            return bot.MarketShift is null || now < bot.MarketShift.UntilUtc;
        }

        return bot.MarketShift is null;
    }

    /// <summary>
    /// Determines whether a bot's gear is good enough for the goal, relative to its level.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="goal">The goal of the party.</param>
    /// <returns><c>true</c>, if the score reaches the goal's expected share of the bot's level.</returns>
    internal static bool HasGearFor(OfflinePlayer bot, BotPartyGoal goal)
    {
        var perLevel = goal switch
        {
            BotPartyGoal.BossAttempt => BotPartyPolicy.BossAttemptGearScorePerLevel,
            BotPartyGoal.MarketEscort => BotPartyPolicy.EscortGearScorePerLevel,
            _ => BotPartyPolicy.EventGearScorePerLevel,
        };

        return GetGearScore(bot) >= GetLevel(bot) * perLevel;
    }

    /// <summary>
    /// Gets a rough readiness value used to pick the leader of a boss attempt or an escort: gear first,
    /// level second - the strongest bot should be the one the others follow.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The readiness value.</returns>
    internal static double GetReadiness(OfflinePlayer bot)
        => GetGearScore(bot) + GetLevel(bot);
}
