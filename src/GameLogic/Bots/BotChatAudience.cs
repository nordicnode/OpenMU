// <copyright file="BotChatAudience.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Decides which bot talks to which player: who greets an entering player, who may say something
/// nobody asked for, and which two bots are close enough to have a conversation. Pure logic -
/// exposed for unit tests.
/// </summary>
internal static class BotChatAudience
{
    /// <summary>
    /// How close a bot must stand to a player to talk to them - to greet an entering player, or to
    /// let them overhear a conversation: roughly the range in which the player sees it, so the line
    /// comes from a character on the screen.
    /// </summary>
    internal const double HearingRange = 12;

    /// <summary>
    /// How close two bots must stand to each other to have a conversation - close enough to look like
    /// they are talking to each other.
    /// </summary>
    internal const double ConversationPairRange = 5;

    /// <summary>
    /// Determines whether the player is a server-side bot.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c>, if the player is a bot.</returns>
    internal static bool IsBot(Player player) => player is OfflinePlayer && player.Account?.IsBot == true;

    /// <summary>
    /// Determines whether the bot is in the world and free to talk: alive and not inside an event.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns><c>true</c>, if the bot may talk right now.</returns>
    internal static bool IsAvailable(Player bot)
        => bot.PlayerState.CurrentState == PlayerState.EnteredWorld
           && bot.IsAlive
           && bot.CurrentMiniGame is null;

    /// <summary>
    /// Determines whether the player stands in the safezone of its map.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c>, if the player is in a safezone.</returns>
    internal static bool IsInSafezone(Player player)
        => player.CurrentMap is { } map && map.Terrain.SafezoneMap[player.Position.X, player.Position.Y];

    /// <summary>
    /// Determines whether the listener is close enough to the speaker to hear it.
    /// </summary>
    /// <param name="speaker">The speaker.</param>
    /// <param name="listener">The listener.</param>
    /// <returns><c>true</c>, if both are on the same map and within <see cref="HearingRange"/>.</returns>
    internal static bool IsInHearingRange(Player speaker, Player listener)
        => ReferenceEquals(speaker.CurrentMap, listener.CurrentMap) && speaker.GetDistanceTo(listener) <= HearingRange;

    /// <summary>
    /// Gets the level of a player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The level, 0 if the player has none.</returns>
    internal static int GetLevel(Player player) => (int)(player.Attributes?[Stats.Level] ?? 0);

    /// <summary>
    /// Determines whether the bot is free to wait for players at an event: not already waiting, not on
    /// an errand, a market shift or a revenge, and not in a party with a player - it would look for
    /// company it has.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns><c>true</c>, if the bot may set off to a meeting place.</returns>
    internal static bool IsFreeToMeet(OfflinePlayer bot)
        => bot.EventMeeting is null
           && bot.MarketShift is null
           && !bot.IsOnShoppingTrip
           && !bot.IsOnBuffTrip
           && !bot.IsOnChaosMachineTrip
           && !bot.HasRevengeIntent
           && !BotPartyHandler.HasHumanCompanion(bot);

    /// <summary>
    /// Finds the bot which talks to a player - greets the entering player, congratulates them: the
    /// nearest available bot on the player's map, within the range in which the player sees it.
    /// </summary>
    /// <param name="players">The players of the game server.</param>
    /// <param name="player">The entering player.</param>
    /// <param name="map">The player's map.</param>
    /// <returns>The greeting bot, or <c>null</c> when none is close enough.</returns>
    internal static Player? FindGreeter(IEnumerable<Player> players, Player player, GameMap map)
        => players
            .Where(p => IsBot(p) && IsAvailable(p) && ReferenceEquals(p.CurrentMap, map))
            .Select(p => (Bot: p, Distance: p.GetDistanceTo(player)))
            .Where(candidate => candidate.Distance <= HearingRange)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Bot)
            .FirstOrDefault();

    /// <summary>
    /// Finds two bots for a conversation among the bots near a player: a random pair standing close
    /// together, both in a safezone (a town) or both outside of it (out hunting).
    /// </summary>
    /// <param name="bots">The bots which can hear the player.</param>
    /// <returns>The pair and where it stands, or <c>null</c> when there is none.</returns>
    internal static (Player First, Player Second, BotConversationPlace Place)? FindConversationPair(IReadOnlyList<Player> bots)
    {
        var pairs = new List<(Player First, Player Second, BotConversationPlace Place)>();
        for (var i = 0; i < bots.Count; i++)
        {
            for (var j = i + 1; j < bots.Count; j++)
            {
                var (a, b) = (bots[i], bots[j]);
                var inTown = IsInSafezone(a);
                if (ReferenceEquals(a.CurrentMap, b.CurrentMap)
                    && a.GetDistanceTo(b) <= ConversationPairRange
                    && inTown == IsInSafezone(b))
                {
                    var place = inTown ? BotConversationPlace.Town : BotConversationPlace.Hunting;

                    // Random order, so it's not always the same bot which starts.
                    pairs.Add(Rand.NextRandomBool() ? (a, b, place) : (b, a, place));
                }
            }
        }

        return pairs.Count == 0 ? null : pairs[Rand.NextInt(0, pairs.Count)];
    }

    /// <summary>
    /// Determines whether a human player observes the bot, so that talking to nobody is not wasted.
    /// </summary>
    /// <param name="bot">The bot.</param>
    /// <returns><c>true</c>, if at least one human player observes the bot.</returns>
    internal static async ValueTask<bool> HasHumanObserverAsync(Player bot)
    {
        var seen = false;
        await bot.ForEachObservingAsync<Player>(
            observer =>
            {
                seen |= observer is not OfflinePlayer;
                return ValueTask.CompletedTask;
            },
            false).ConfigureAwait(false);
        return seen;
    }
}
