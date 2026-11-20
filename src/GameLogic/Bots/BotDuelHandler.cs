// <copyright file="BotDuelHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Duel;

/// <summary>
/// Lets a bot take part in the game's own duel system - only when the operator enabled it
/// (<see cref="BotConfiguration.Duels"/>) and only against a player of a comparable level. A duel is
/// consensual by design: the bot accepts a challenge it can answer, and now and then challenges a
/// nearby player itself. It never duels while it is busy with an errand, an event or a grudge.
/// </summary>
internal static class BotDuelHandler
{
    /// <summary>The highest level difference at which a duel still feels fair to both sides.</summary>
    internal const int MaximumLevelDifference = 30;

    /// <summary>The range (tiles) in which a bot looks for a player to challenge.</summary>
    internal const int ChallengeRange = 20;

    /// <summary>How often a bot considers challenging a nearby player.</summary>
    internal static readonly TimeSpan ChallengeInterval = TimeSpan.FromMinutes(2);

    /// <summary>Chance of an offered challenge on a check, so not every bot asks every two minutes.</summary>
    private const int ChallengeChancePercent = 35;

    /// <summary>
    /// Determines whether duels are enabled for the bot's server.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns><c>true</c> if the operator enabled duels.</returns>
    internal static bool IsDuelEnabled(Player bot)
        => bot.GameContext.FeaturePlugIns.GetPlugIn<BotFeaturePlugIn>()?.Configuration?.Duels == true;

    /// <summary>
    /// Gets the duel partner of the bot, or <c>null</c> when it isn't in a duel.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The other duelist of the bot's duel room, or <c>null</c>.</returns>
    internal static Player? GetOpponent(Player bot)
        => bot.DuelRoom is { } room && room.IsDuelist(bot)
            ? ReferenceEquals(room.Requester, bot) ? room.Opponent : room.Requester
            : null;

    /// <summary>
    /// Determines whether the bot may answer the player's duel request now.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="requester">The player who sent the request.</param>
    /// <returns><c>true</c> if the bot accepts.</returns>
    internal static bool CanAccept(Player bot, Player requester)
        => IsDuelEnabled(bot) && IsFree(bot, allowPendingRequest: true) && IsFairOpponent(bot, requester, allowPendingRequest: true);

    /// <summary>
    /// Accepts a pending duel request, if it is still the one the player sent and still acceptable.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="requester">The player who sent the request.</param>
    /// <returns><c>true</c> if the duel was accepted.</returns>
    internal static async ValueTask<bool> TryAcceptAsync(Player bot, Player requester)
    {
        if (!CanAccept(bot, requester)
            || bot.DuelRoom is not { State: DuelState.DuelRequested } room
            || !ReferenceEquals(room.Requester, requester)

            // Only a request which was sent to the bot is the bot's to answer; a challenge it started
            // itself is the other side's turn.
            || !ReferenceEquals(room.Opponent, bot))
        {
            return false;
        }

        await new DuelActions().HandleDuelResponseAsync(bot, requester, true).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Offers a duel to a nearby player of comparable level, now and then. The request goes to the
    /// player like any other duel request, so it is the player who decides.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> if a request was sent.</returns>
    internal static async ValueTask<bool> TryChallengeNearbyPlayerAsync(Player bot, DateTime now)
    {
        if (!IsDuelEnabled(bot)
            || !IsFree(bot)
            || bot.CurrentMap is not { } map
            || !Rand.NextRandomBool(ChallengeChancePercent))
        {
            return false;
        }

        var candidate = map.GetAttackablesInRange(bot.Position, ChallengeRange)
            .OfType<Player>()
            .Where(player => IsFairOpponent(bot, player))
            .OrderBy(player => Math.Abs(player.Level - bot.Level))
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        await new DuelActions().HandleDuelRequestAsync(bot, candidate).ConfigureAwait(false);
        bot.Logger.LogDebug("Bot '{Name}' challenges '{Player}' to a duel.", bot.Name, candidate.Name);
        return true;
    }

    /// <summary>
    /// Determines whether the bot can start a duel at all: in the world, alive, and not busy with an
    /// errand, an event, a market shift or a grudge.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="allowPendingRequest">
    /// Whether the duel room of a request which is still waiting for the bot's answer counts as free.
    /// A real request reaches the bot with its room already attached (see
    /// <see cref="DuelActions.HandleDuelRequestAsync"/>), so answering it needs the exception. Only a
    /// request addressed to the bot counts - a challenge it sent itself still occupies it.
    /// </param>
    /// <returns><c>true</c> if the bot is free.</returns>
    private static bool IsFree(Player bot, bool allowPendingRequest = false)
    {
        if (bot.Account?.IsBot != true
            || bot.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !bot.IsAlive
            || bot.CurrentMiniGame is not null
            || bot.CurrentMap is null
            || (bot.DuelRoom is { } duelRoom
                && !(allowPendingRequest && duelRoom.State == DuelState.DuelRequested && ReferenceEquals(duelRoom.Opponent, bot))))
        {
            return false;
        }

        if (bot is OfflinePlayer offline)
        {
            return !offline.IsOnShoppingTrip
                   && !offline.IsOnBuffTrip
                   && !offline.IsOnChaosMachineTrip
                   && offline.MarketShift is null
                   && offline.EventMeeting is null
                   && offline.BossAttempt is null
                   && !offline.HasRevengeIntent;
        }

        return true;
    }

    /// <summary>
    /// Determines whether the player is a fair opponent: a human in the same world, near the bot's
    /// level, not a party mate and not already busy with another duel.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="player">The player.</param>
    /// <param name="allowPendingRequest">
    /// Whether the room of a request which is still waiting for the bot's answer counts as no duel yet.
    /// The requester carries the same room when the request arrives (see
    /// <see cref="DuelActions.HandleDuelRequestAsync"/>), so answering it needs the exception.
    /// </param>
    /// <returns><c>true</c> if a duel against the player is fair.</returns>
    private static bool IsFairOpponent(Player bot, Player player, bool allowPendingRequest = false)
    {
        var isThePendingRequest = allowPendingRequest
                                  && bot.DuelRoom is { State: DuelState.DuelRequested } pendingRoom
                                  && ReferenceEquals(pendingRoom, player.DuelRoom)
                                  && ReferenceEquals(pendingRoom.Opponent, bot);
        if (player is OfflinePlayer
            || ReferenceEquals(bot, player)
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !player.IsAlive
            || player.IsAtSafezone()
            || (player.DuelRoom is not null && !isThePendingRequest)
            || player.CurrentMap is null
            || !ReferenceEquals(player.CurrentMap, bot.CurrentMap)
            || Math.Abs(player.Level - bot.Level) > MaximumLevelDifference)
        {
            return false;
        }

        return bot.Party is null || !bot.Party.PartyList.Any(member => ReferenceEquals(member, player));
    }
}
