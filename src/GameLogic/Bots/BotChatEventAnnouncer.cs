// <copyright file="BotChatEventAnnouncer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

/// <summary>
/// Announces opened mini-game events and started invasions, in the normal chat and with <c>/post</c>,
/// and lets a bot wait at the meeting point afterwards (see <see cref="BotEventMeeting"/>).
/// </summary>
/// <remarks>
/// A bot only talks about what it could actually do: it posts about a Blood Castle only when it meets
/// that event's entry requirements (the same check that decides whether a bot may follow its leader in,
/// see <see cref="BotMiniGameHandler.IsEligible"/>), and about an invasion only when it may warp to the
/// invaded map. The limits of the <see cref="BotChatConfiguration"/> keep a large population from
/// flooding the chat; they are tracked per game server.
/// </remarks>
internal sealed class BotChatEventAnnouncer
{
    /// <summary>
    /// How long an invasion counts as announced. Long enough that a second invasion of the same kind
    /// started by the same schedule run doesn't get its own post.
    /// </summary>
    private static readonly TimeSpan InvasionAnnouncementWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The least time an event's entrance must still stay open for a bot to announce it: the bot
    /// waits at the meeting point until the entrance closes (see <see cref="BotEventMeeting"/>), and a
    /// player who reads the post needs the time to get there and invite it.
    /// </summary>
    private static readonly TimeSpan MinimumMeetingTime = TimeSpan.FromMinutes(2);

    private readonly Func<BotChatConfiguration?> _configuration;
    private readonly Func<IGameContext, BotChatThrottle> _getThrottle;
    private readonly Func<Player, BotChatConfiguration, bool> _mayTalkUnasked;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotChatEventAnnouncer"/> class.
    /// </summary>
    /// <param name="configuration">Gets the current configuration of the chat plugin.</param>
    /// <param name="getThrottle">Gets the chat limits and memory of a game server.</param>
    /// <param name="mayTalkUnasked">Determines whether a bot may say something nobody asked for.</param>
    internal BotChatEventAnnouncer(
        Func<BotChatConfiguration?> configuration,
        Func<IGameContext, BotChatThrottle> getThrottle,
        Func<Player, BotChatConfiguration, bool> mayTalkUnasked)
    {
        this._configuration = configuration;
        this._getThrottle = getThrottle;
        this._mayTalkUnasked = mayTalkUnasked;
    }

    /// <summary>
    /// Announces an opened mini-game event: a reminder in each town, and a server-wide post naming the
    /// bot which waits at the meeting point.
    /// </summary>
    /// <param name="miniGame">The mini game which opened.</param>
    /// <returns>The task.</returns>
    internal ValueTask MiniGameEntranceOpenedAsync(MiniGameContext miniGame)
    {
        if (this._configuration() is not { } reminderConfiguration
            || miniGame.Definition.Type is not (MiniGameType.BloodCastle or MiniGameType.DevilSquare or MiniGameType.ChaosCastle))
        {
            return ValueTask.CompletedTask;
        }

        if (reminderConfiguration.EventReminderChancePercent > 0)
        {
            BotChatPlugIn.RunLater(miniGame.GameContext, BotChatTiming.RandomDelay(BotChatTiming.EventReminderDelaySeconds), () => this.RemindOfEventAsync(miniGame, reminderConfiguration));
        }

        if (this._configuration() is not { EventPostChancePercent: > 0 } configuration
            || !IsPostCommandActive(miniGame.GameContext))
        {
            return ValueTask.CompletedTask;
        }

        // Every level of an event (Blood Castle 1..8) reports its own opening. Each one waits a random
        // time; the first one which finds a qualified bot takes the announcement of the whole event type.
        BotChatPlugIn.RunLater(
            miniGame.GameContext,
            BotChatTiming.RandomDelay(TimeSpan.Zero, configuration.MaxPostDelay),
            () => this.PostAboutEventAsync(miniGame, configuration));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Announces a started invasion with a server-wide post, naming a bot which may join it on a map
    /// the invasion reaches.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="invasion">The invasion.</param>
    /// <param name="maps">The maps the invasion reaches.</param>
    /// <returns>The task.</returns>
    internal ValueTask InvasionStartedAsync(IGameContext gameContext, IPeriodicTaskPlugIn invasion, IReadOnlyCollection<GameMapDefinition> maps)
    {
        if (this._configuration() is not { InvasionPostChancePercent: > 0 } configuration
            || maps.Count == 0
            || !IsPostCommandActive(gameContext))
        {
            return ValueTask.CompletedTask;
        }

        BotChatPlugIn.RunLater(
            gameContext,
            BotChatTiming.RandomDelay(TimeSpan.Zero, configuration.MaxPostDelay),
            () => this.PostAboutInvasionAsync(gameContext, invasion, maps, configuration));
        return ValueTask.CompletedTask;
    }

    private static bool IsPostCommandActive(IGameContext gameContext)
        => gameContext.PlugInManager.IsPlugInActive(typeof(PostChatCommandPlugIn));

    /// <summary>
    /// Gets the display name of an invasion in the receiver's language, from the name of its plugin.
    /// </summary>
    private static string GetInvasionName(IPeriodicTaskPlugIn invasion, CultureInfo culture)
    {
        var displayName = (Attribute.GetCustomAttribute(invasion.GetType(), typeof(DisplayAttribute)) as DisplayAttribute)?.Name;
        return displayName is null
            ? invasion.GetType().Name
            : PlugInResources.ResourceManager.GetString(displayName, culture) ?? displayName;
    }

    /// <summary>
    /// Lets a bot in each town where a player stands remind of the opened event in the normal chat -
    /// "bc open, anyone going?" - without a server-wide post. Like the post, only a bot which meets the
    /// event's entry requirements says it, and only once per town and opening.
    /// </summary>
    private async ValueTask RemindOfEventAsync(MiniGameContext miniGame, BotChatConfiguration configuration)
    {
        var now = DateTime.UtcNow;
        if (now >= miniGame.EnterEndsAtUtc)
        {
            return;
        }

        var definition = miniGame.Definition;
        var throttle = this._getThrottle(miniGame.GameContext);
        var players = await miniGame.GameContext.GetPlayersAsync().ConfigureAwait(false);
        var humansInTown = players.Where(p => p is not OfflinePlayer && p.PlayerState.CurrentState == PlayerState.EnteredWorld && BotChatAudience.IsInSafezone(p)).ToList();
        foreach (var human in humansInTown)
        {
            if (human.CurrentMap is not { } map
                || BotChatAudience.FindGreeter(players.Where(p => p is OfflinePlayer candidate && BotMiniGameHandler.IsEligible(candidate, definition, out _)), human, map) is not { } bot
                || !this._mayTalkUnasked(bot, configuration)
                || !throttle.IsCategoryDue(bot, BotChatCategory.EventReminder, now, configuration.GetCategoryCooldown(BotChatCategory.EventReminder))
                || !throttle.TryReserveOccasion($"event reminder {definition.Type} {map.MapId}", now, miniGame.EnterEndsAtUtc - now)
                || !Rand.NextRandomBool(configuration.EventReminderChancePercent)
                || BotChatReplySelector.PickLineFor(throttle, bot, configuration.EventReminderLines) is not { } line
                || !throttle.TryReserveChat(bot, now, configuration.BotChatCooldown, category: BotChatCategory.EventReminder, categoryCooldown: configuration.GetCategoryCooldown(BotChatCategory.EventReminder)))
            {
                continue;
            }

            var values = BotChatValues.Create(bot);
            values["event"] = culture => definition.Name.GetTranslation(culture) ?? string.Empty;
            await BotChatSpeaker.SayAsync(bot, line.Text, values, gesture: line.Gesture).ConfigureAwait(false);
        }
    }

    private async ValueTask PostAboutEventAsync(MiniGameContext miniGame, BotChatConfiguration configuration)
    {
        var now = DateTime.UtcNow;
        if (now >= miniGame.EnterEndsAtUtc)
        {
            return;
        }

        // A player reading the post needs the time to get to the meeting point.
        if (miniGame.EnterEndsAtUtc - now < MinimumMeetingTime)
        {
            return;
        }

        var definition = miniGame.Definition;
        var players = await miniGame.GameContext.GetPlayersAsync().ConfigureAwait(false);
        var candidates = players
            .OfType<OfflinePlayer>()
            .Where(bot => BotChatAudience.IsBot(bot)
                          && BotChatAudience.IsAvailable(bot)
                          && BotChatAudience.IsFreeToMeet(bot)
                          && BotMiniGameHandler.IsEligible(bot, definition, out _))
            .Select(bot => (Bot: bot, Meeting: BotEventMeetingPlanner.TryPlan(bot, definition.Type, miniGame.EnterEndsAtUtc)))
            .Where(candidate => candidate.Meeting is not null)
            .ToList();
        var throttle = this._getThrottle(miniGame.GameContext);
        if (candidates.Count == 0)
        {
            return;
        }

        var (chosen, meeting) = candidates[Rand.NextInt(0, candidates.Count)];
        if (!throttle.TryReserveOccasion($"mini game {definition.Type}", now, miniGame.EnterEndsAtUtc - now)
            || !Rand.NextRandomBool(configuration.EventPostChancePercent)
            || BotChatReplySelector.PickLineFor(throttle, chosen, configuration.EventLines) is not { } line
            || !throttle.TryReservePost(now, configuration.MinTimeBetweenPosts))
        {
            return;
        }

        chosen.EventMeeting = meeting! with { Event = definition };

        // The other bots which wait at the same place become its event party: one invitation then takes
        // the whole group in (see BotPartyHandler.TryMergeEventPartyAsync).
        foreach (var candidate in candidates
                     .Where(c => !ReferenceEquals(c.Bot, chosen)
                                 && c.Bot.Party is null
                                 && c.Meeting!.Map.Number == meeting!.Map.Number)
                     .Take(BotPartyPolicy.MaxPartySize - 1))
        {
            candidate.Bot.EventMeeting = candidate.Meeting! with { Event = definition };
        }

        var values = BotChatValues.Create(chosen);
        values["event"] = culture => definition.Name.GetTranslation(culture) ?? string.Empty;
        values["map"] = culture => meeting!.Map.Name.GetTranslation(culture) ?? string.Empty;
        values["location"] = culture => BotChatComposer.FormatLocation(meeting!.Map.Name.GetTranslation(culture), meeting.NpcPosition);
        await BotChatSpeaker.PostAsync(chosen, line.Text, values).ConfigureAwait(false);
    }

    private async ValueTask PostAboutInvasionAsync(IGameContext gameContext, IPeriodicTaskPlugIn invasion, IReadOnlyCollection<GameMapDefinition> maps, BotChatConfiguration configuration)
    {
        var now = DateTime.UtcNow;
        var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
        var candidates = players
            .Where(p => BotChatAudience.IsBot(p) && BotChatAudience.IsAvailable(p))
            .SelectMany(p => maps.Where(map => BotEventMeetingPlanner.MayWarpTo(p, map)).Select(map => (Bot: p, Map: map)))
            .ToList();
        var throttle = this._getThrottle(gameContext);
        if (candidates.Count == 0)
        {
            return;
        }

        var (bot, map) = candidates[Rand.NextInt(0, candidates.Count)];
        if (!throttle.TryReserveOccasion($"invasion {invasion.GetType().Name}", now, InvasionAnnouncementWindow)
            || !Rand.NextRandomBool(configuration.InvasionPostChancePercent)
            || BotChatReplySelector.PickLineFor(throttle, bot, configuration.InvasionLines) is not { } line
            || !throttle.TryReservePost(now, configuration.MinTimeBetweenPosts))
        {
            return;
        }

        // A few bots which may reach the invaded map make it their goal and group up for the attempt.
        // The rest keep hunting; an invasion is an opportunity, not a draft.
        var bossAttemptBots = candidates
            .Where(candidate => candidate.Map.Number == map.Number
                                && candidate.Bot is OfflinePlayer bot
                                && (bot.Party is null || bot.Party.PartyList.All(member => member is Player { Account.IsBot: true })))
            .Select(candidate => (OfflinePlayer)candidate.Bot)
            .Distinct()
            .Where(candidate => BotPartyReadiness.IsAvailable(candidate, BotPartyGoal.BossAttempt, now)
                                && BotPartyReadiness.HasGearFor(candidate, BotPartyGoal.BossAttempt))
            .OrderByDescending(BotPartyReadiness.GetReadiness)
            .Take(BotPartyPolicy.MaxPartySize)
            .ToList();
        var until = now + BotPartyPolicy.BossAttemptDuration;
        foreach (var candidate in bossAttemptBots)
        {
            candidate.BossAttempt = new BotBossAttempt(map, until);
        }

        var values = BotChatValues.Create(bot);
        values["event"] = culture => GetInvasionName(invasion, culture);
        values["map"] = culture => map.Name.GetTranslation(culture) ?? string.Empty;
        values["location"] = values["map"];
        await BotChatSpeaker.PostAsync(bot, line.Text, values).ConfigureAwait(false);
    }
}
