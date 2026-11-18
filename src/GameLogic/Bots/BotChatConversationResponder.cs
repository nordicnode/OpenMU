// <copyright file="BotChatConversationResponder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Answers a player's message to a bot where the answer is a conversation rather than a one-line reply:
/// so far the negotiation about where to meet (see <see cref="BotEventMeeting"/>).
/// </summary>
/// <remarks>
/// The lookup of the place the player names for the meeting lives here as a rule
/// (<see cref="ResolveMeetingPlace"/>), because its precedence - a named map wins over "here" - decides
/// whether a bot agrees to meet a player at all.
/// </remarks>
internal sealed class BotChatConversationResponder
{
    private readonly Func<IGameContext, BotChatThrottle> _getThrottle;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotChatConversationResponder"/> class.
    /// </summary>
    /// <param name="getThrottle">Gets the chat throttle of a game server.</param>
    public BotChatConversationResponder(Func<IGameContext, BotChatThrottle> getThrottle)
        => this._getThrottle = getThrottle;

    /// <summary>
    /// Resolves the place a player names when arranging a meeting: a named map or warp wins over "here",
    /// because "i'm here, devias 215 40" names the place; a bare "here" means the map the player stands on.
    /// </summary>
    /// <param name="message">The message of the player.</param>
    /// <param name="configuration">The chat configuration with the keywords for "here".</param>
    /// <param name="requesterMap">The map the player stands on; <c>null</c> if it is unknown.</param>
    /// <param name="requesterPosition">The position of the player.</param>
    /// <param name="maps">The maps of the game, with the names players use for them.</param>
    /// <param name="warps">The warps of the game, whose names players use for the maps they reach.</param>
    /// <returns>The place, or <c>null</c> if the message names none.</returns>
    internal static (GameMapDefinition Map, WarpInfo? Warp, Point? Position)? ResolveMeetingPlace(
        string message,
        BotChatConfiguration configuration,
        GameMapDefinition? requesterMap,
        Point requesterPosition,
        IEnumerable<GameMapDefinition> maps,
        IEnumerable<WarpInfo> warps)
    {
        var place = BotChatComposer.FindPlace(message, maps, warps);
        if (place is null
            && requesterMap is not null
            && BotChatComposer.ContainsAnyKeyword(message, configuration.PartyHereKeywords))
        {
            return (requesterMap, null, requesterPosition);
        }

        return place;
    }

    /// <summary>
    /// Answers the player's suggestion where to meet: it names the place back, and either walks to its
    /// entry NPC and waits there (the meeting, see <see cref="BotEventMeeting"/>) or explains that it can't
    /// go there - in which case the player may name another place until the offer expires.
    /// </summary>
    /// <param name="bot">The bot which answers.</param>
    /// <param name="requester">The player who asked.</param>
    /// <param name="message">The message which contains the place.</param>
    /// <param name="configuration">The chat configuration.</param>
    internal void AnswerMeetingPlace(OfflinePlayer bot, Player requester, string message, BotChatConfiguration configuration)
    {
        var throttle = this._getThrottle(bot.GameContext);
        var values = BotChatValues.Create(bot);
        var requesterName = requester.SelectedCharacter?.Name ?? string.Empty;
        values["player"] = _ => requesterName;
        var language = BotChatComposer.GetLanguage(message, configuration);
        var place = ResolveMeetingPlace(
            message,
            configuration,
            requester.CurrentMap?.Definition,
            requester.Position,
            bot.GameContext.Configuration.Maps,
            bot.GameContext.Configuration.WarpList);

        if (place is not { } found)
        {
            if (throttle.TryReserveWhisperReply(bot, DateTime.UtcNow, configuration.WhisperReplyCooldown)
                && BotChatReplySelector.PickLineFor(throttle, requester, configuration.PartyAskWhereLines) is { } askLine)
            {
                WhisperLater(bot, requester, askLine, values, language);
            }

            return;
        }

        var (map, warp, position) = found;

        // "devias3" is told back as the player wrote it, not as the map ("Devias").
        var placeName = warp?.Name ?? map.Name;
        values["map"] = culture => placeName.GetTranslation(culture) ?? string.Empty;
        values["location"] = culture => BotChatComposer.FormatLocation(placeName.GetTranslation(culture), position);
        if (!BotChatAudience.IsAvailable(bot) || !BotChatAudience.IsFreeToMeet(bot))
        {
            // Taken into another party, or busy otherwise, since it answered: it lets the player be.
            throttle.RemovePartyOffer(bot);
            return;
        }

        if (BotEventMeetingPlanner.FindMeetingEntry(bot, map, warp, position) is not { } entry)
        {
            // The offer stays open: the player may name another place until it expires.
            if (BotChatComposer.PickCurrentLine(configuration.PartyCantGoLines) is { } cantGoLine)
            {
                WhisperLater(bot, requester, cantGoLine, values, language);
            }

            return;
        }

        throttle.RemovePartyOffer(bot);
        bot.EventMeeting = new BotEventMeeting(map, entry.Position, DateTime.UtcNow + configuration.PartyMeetingWaitTime, entry.Gate);
        if (BotChatComposer.PickCurrentLine(configuration.PartyOnTheWayLines) is { } onTheWayLine)
        {
            WhisperLater(bot, requester, onTheWayLine, values, language);
        }
    }

    /// <summary>
    /// Whispers a line to the player after the typing delay, as it runs in the MU Helper tick.
    /// </summary>
    /// <param name="bot">The bot which speaks.</param>
    /// <param name="receiver">The player who gets the line.</param>
    /// <param name="line">The line to say.</param>
    /// <param name="values">The values which the line may reference.</param>
    /// <param name="language">The language of the line; <c>null</c> for the default one.</param>
    private static void WhisperLater(Player bot, Player receiver, BotChatLine line, Dictionary<string, Func<CultureInfo, string>> values, CultureInfo? language)
        => BotChatPlugIn.RunLater(
            bot.GameContext,
            BotChatTiming.TypingDelay(line.Text),
            () => receiver.PlayerState.CurrentState == PlayerState.EnteredWorld
                ? BotChatSpeaker.WhisperAsync(bot, receiver, line.Text, values, language, line.Gesture)
                : ValueTask.CompletedTask);
}
