// <copyright file="BotPartyTempo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Keeps a bot in step with a human party leader: while the player runs ahead to pull monsters, the
/// bots don't pick a new fight behind his back - they follow first. Once they caught up (or the
/// leader stands still) they fight as usual. Bot-only parties keep their own tempo, since there is no
/// human to coordinate with.
/// </summary>
internal static class BotPartyTempo
{
    /// <summary>
    /// How far a human leader may run ahead before the bots stop starting new fights - the same
    /// distance the follow logic uses to close back in.
    /// </summary>
    internal const int PullDistance = 12;

    /// <summary>
    /// Determines whether the bot should hold its fire for now: its human party leader is walking
    /// ahead, out of the group's fighting distance, and the bot is not in the middle of a fight.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <param name="hasEngagedTarget">Whether the bot already fights a valid target.</param>
    /// <returns><c>true</c> if the bot should not start a new fight.</returns>
    internal static bool ShouldHoldFire(OfflinePlayer bot, bool hasEngagedTarget)
    {
        if (hasEngagedTarget
            || bot.Account?.IsBot != true
            || bot.CurrentMap is not { } map
            || bot.Party is not { PartyMaster: Player leader }
            || ReferenceEquals(leader, bot)
            || leader is OfflinePlayer
            || leader.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !ReferenceEquals(leader.CurrentMap, map)
            || !leader.IsWalking)
        {
            return false;
        }

        return bot.GetDistanceTo(leader.Position) > PullDistance;
    }
}
