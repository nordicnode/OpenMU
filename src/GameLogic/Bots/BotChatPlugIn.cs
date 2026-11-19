// <copyright file="BotChatPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Duel;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Lets the server-side bots talk: they announce opened events and started invasions with
/// <c>/post</c>, comment on their level milestones and answer whispers - with lines from the
/// configuration, no language model.
/// </summary>
/// <remarks>
/// A bot only talks about what it could actually do: it posts about a Blood Castle only when it
/// meets that event's entry requirements (the same check that decides whether a bot may follow its
/// leader in, see <see cref="BotMiniGameHandler.IsEligible"/>), and about an invasion only when it may
/// warp to the invaded map. After announcing an event, the bot waits at the event's entry NPC (or in
/// its home town for Chaos Castle) until the entrance closes, so the players can find and invite it
/// (see <see cref="BotEventMeeting"/>). The limits of the <see cref="BotChatConfiguration"/> keep a large
/// population from flooding the chat; they are tracked per game server.
/// It's a feature plugin of the bots, listed in the admin panel next to them.
/// </remarks>
[PlugIn]
[Display(Name = nameof(PlugInResources.BotChatPlugIn_Name), Description = nameof(PlugInResources.BotChatPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("B3234CC4-1365-463A-B0DE-603F9D7D86A2")]
public class BotChatPlugIn
    : IFeaturePlugIn,
    IMiniGameEntranceOpenedPlugIn,
    IInvasionStartedPlugIn,
    ICharacterLevelUpPlugIn,
    IWhisperMessageReceivedPlugIn,
    IPlayerStateChangedPlugIn,
    IBotPartyJoinedPlugIn,
    IMiniGameEndedPlugIn,
    IAttackableGotKilledPlugIn,
    IChatMessageSentPlugIn,
    IPeriodicTaskPlugIn,
    IPostMessageSentPlugIn,
    ITradeRequestedPlugIn,
    IDuelRequestedPlugIn,
    IMonsterItemDroppedPlugIn,
    IObjectAddedToMapPlugIn,
    IBotWingsEarnedPlugIn,
    IItemMovedPlugIn,
    ISupportCustomConfiguration<BotChatConfiguration>,
    ISupportDefaultCustomConfiguration,
    IDisabledByDefault
{
    /// <summary>
    /// How long a notable monster on a map counts as announced: an invasion spawns many of them at
    /// once, and a bot shouldn't call out each one.
    /// </summary>
    private static readonly TimeSpan NotableMonsterAnnouncementWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after the last line of a bots' conversation a player's message near them counts as
    /// chiming in - the time to read it and type an answer.
    /// </summary>
    private static readonly TimeSpan ConversationChimeInWindow = TimeSpan.FromSeconds(30);

    private readonly ConditionalWeakTable<IGameContext, BotChatThrottle> _throttles = new();
    private readonly BotChatEventAnnouncer _events;
    private readonly BotChatConversationResponder _conversations;

    /// <summary>
    /// The parsed <see cref="BotChatConfiguration.NotableMonsterNumbers"/>, with the text they were
    /// parsed from.
    /// </summary>
    private (string? Text, HashSet<short> Numbers) _notableMonsters = (null, []);

    /// <summary>
    /// The number of calls of <see cref="ForceStart"/>; a server which didn't see the current number
    /// yet lets a bot advertise like a merchant without waiting for the interval.
    /// </summary>
    private int _forceStartCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotChatPlugIn"/> class.
    /// </summary>
    public BotChatPlugIn()
    {
        this._events = new BotChatEventAnnouncer(() => this.Configuration, this.GetThrottle, this.MayTalkUnasked);
        this._conversations = new BotChatConversationResponder(this.GetThrottle);
    }

    /// <inheritdoc />
    public BotChatConfiguration? Configuration { get; set; }

    /// <inheritdoc />
    public object CreateDefaultConfig() => BotChatConfiguration.CreateDefault();

    /// <inheritdoc />
    public ValueTask MiniGameEntranceOpenedAsync(MiniGameContext miniGame) => this._events.MiniGameEntranceOpenedAsync(miniGame);

    /// <inheritdoc />
    public ValueTask InvasionStartedAsync(IGameContext gameContext, IPeriodicTaskPlugIn invasion, IReadOnlyCollection<GameMapDefinition> maps)
        => this._events.InvasionStartedAsync(gameContext, invasion, maps);

    /// <inheritdoc />
    public void CharacterLeveledUp(Player player)
    {
        if (this.Configuration is not { } configuration)
        {
            return;
        }

        if (player is not OfflinePlayer)
        {
            this.Congratulate(player, configuration, configuration.CongratulationLines, $"congratulation {player.SelectedCharacter?.Name}");
            return;
        }

        if (configuration is not { LevelMilestoneStep: > 0, LevelMilestoneChancePercent: > 0 }
            || !BotChatAudience.IsBot(player))
        {
            return;
        }

        var level = BotChatAudience.GetLevel(player);
        if (level < configuration.LevelMilestoneStep
            || level % configuration.LevelMilestoneStep != 0
            || (configuration.WingsChancePercent > 0 && BotWingHandler.PlanNextGrant(player) is not null)
            || !Rand.NextRandomBool(configuration.LevelMilestoneChancePercent)
            || BotChatComposer.PickCurrentLine(configuration.LevelMilestoneLines) is not { } line
            || !this.GetThrottle(player.GameContext).TryReserveChat(player, DateTime.UtcNow, configuration.BotChatCooldown))
        {
            return;
        }

        RunLater(
            player.GameContext,
            BotChatTiming.RandomDelay(BotChatTiming.LevelMilestoneDelaySeconds),
            () => BotChatAudience.IsAvailable(player) && this.MayTalkUnasked(player, configuration)
                ? BotChatSpeaker.SayAsync(player, line.Text, BotChatValues.Create(player), gesture: line.Gesture)
                : ValueTask.CompletedTask);
    }

    /// <inheritdoc />
    public void WhisperMessageReceived(Player sender, Player receiver, string message, CancelEventArgs cancelEventArgs)
    {
        // A bot which answered the sender's party search waits for the meeting place - that whisper is
        // part of a conversation the player started, so it's always answered.
        if (this.Configuration is { } partyConfiguration
            && receiver is OfflinePlayer partyBot
            && BotChatAudience.IsBot(partyBot)
            && sender is not OfflinePlayer
            && this.GetThrottle(receiver.GameContext).GetOpenPartyOffer(partyBot, DateTime.UtcNow) is { } offer
            && ReferenceEquals(offer.Requester, sender))
        {
            this._conversations.AnswerMeetingPlace(partyBot, sender, message, partyConfiguration);
            return;
        }

        // A bot which is under way to a meeting or already waits there has a concrete answer: where it
        // is, and that the player may come and invite it - instead of a random small-talk line.
        if (this.Configuration is { } meetingConfiguration
            && receiver is OfflinePlayer meetingBot
            && BotChatAudience.IsBot(meetingBot)
            && sender is not OfflinePlayer
            && meetingBot.EventMeeting is { IsActive: true } meeting
            && this._conversations.AnswerMeetingStatus(meetingBot, sender, message, meeting, meetingConfiguration))
        {
            return;
        }

        if (this.Configuration is not { WhisperReplyChancePercent: > 0 } configuration
            || !BotChatAudience.IsBot(receiver)
            || sender is OfflinePlayer
            || !Rand.NextRandomBool(configuration.WhisperReplyChancePercent))
        {
            return;
        }

        var throttle = this.GetThrottle(receiver.GameContext);
        if ((BotChatReplySelector.PickMerchantReplyFor(receiver, throttle, sender, message, configuration)
             ?? BotChatReplySelector.PickReplyFor(throttle, sender, configuration.WhisperReplies, message)) is not { } line
            || !throttle.TryReserveWhisperReply(receiver, DateTime.UtcNow, configuration.WhisperReplyCooldown))
        {
            return;
        }

        var text = GetAnswerText(receiver, line.Text, configuration);
        var values = BotChatValues.Create(receiver);
        var senderName = sender.SelectedCharacter?.Name ?? string.Empty;
        values["player"] = _ => senderName;
        var language = BotChatComposer.GetLanguage(message, configuration);
        RunLater(
            receiver.GameContext,
            BotChatTiming.TypingDelay(text),
            () => BotChatAudience.IsAvailable(receiver) && sender.PlayerState.CurrentState == PlayerState.EnteredWorld
                ? BotChatSpeaker.WhisperAsync(receiver, sender, text, values, language, line.Gesture)
                : ValueTask.CompletedTask);
    }

    /// <inheritdoc />
    public ValueTask PlayerStateChangedAsync(Player player, State previousState, State currentState)
    {
        // Only entering the game counts - a warp or a map change keeps the player in the world.
        if (this.Configuration is not { GreetingChancePercent: > 0 } configuration
            || player is OfflinePlayer
            || currentState != PlayerState.EnteredWorld
            || previousState != PlayerState.CharacterSelection
            || !Rand.NextRandomBool(configuration.GreetingChancePercent))
        {
            return ValueTask.CompletedTask;
        }

        RunLater(player.GameContext, BotChatTiming.RandomDelay(BotChatTiming.GreetingDelaySeconds), () => this.GreetAsync(player, configuration));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void BotJoinedParty(Player bot, Player inviter)
    {
        if (this.Configuration is not { } partyConfiguration)
        {
            return;
        }

        // An old friend gets a warmer welcome; and from now on, the inviter and the party are friends.
        var throttle = this.GetThrottle(bot.GameContext);
        var inviterIsFriend = inviter.SelectedCharacter?.Name is { } name && throttle.IsFriend(bot, name);
        throttle.RememberFriends(bot, (bot.Party?.PartyList ?? []).OfType<Player>().Append(inviter).Where(p => p is not OfflinePlayer).Select(p => p.SelectedCharacter?.Name).OfType<string>());

        if (partyConfiguration is not { PartyChatChancePercent: > 0 } configuration
            || !Rand.NextRandomBool(configuration.PartyChatChancePercent)
            || bot.Party is not { } party
            || BotChatComposer.PickCurrentLine(inviterIsFriend && configuration.FriendLines.Count > 0 ? configuration.FriendLines : configuration.PartyJoinedLines) is not { } line
            || !throttle.TryReserveChat(bot, DateTime.UtcNow, configuration.BotChatCooldown))
        {
            return;
        }

        var values = BotChatValues.Create(bot);
        var inviterName = inviter.SelectedCharacter?.Name ?? string.Empty;
        values["player"] = _ => inviterName;
        RunLater(bot.GameContext, BotChatTiming.TypingDelay(line.Text), () => BotChatSpeaker.PartySayAsync(bot, party, line.Text, values, gesture: line.Gesture, toward: inviter));
    }

    /// <inheritdoc />
    public ValueTask MiniGameEndedAsync(MiniGameContext miniGame, Player? winner, IReadOnlyCollection<Player> finishers)
    {
        if (this.Configuration is not { PartyChatChancePercent: > 0 } configuration)
        {
            return ValueTask.CompletedTask;
        }

        // One bot per party with a player: a whole chorus of "gg" from the bots would give them away.
        var speakers = finishers
            .Where(p => BotChatAudience.IsBot(p) && p.Party is not null && BotPartyHandler.HasHumanCompanion(p))
            .GroupBy(p => p.Party)
            .Select(party => party.First())
            .ToList();
        var definition = miniGame.Definition;
        foreach (var bot in speakers)
        {
            if (bot.Party is not { } party
                || !Rand.NextRandomBool(configuration.PartyChatChancePercent)
                || BotChatComposer.PickCurrentLine(configuration.EventFinishedLines) is not { } line
                || !this.GetThrottle(bot.GameContext).TryReserveChat(bot, DateTime.UtcNow, configuration.BotChatCooldown))
            {
                continue;
            }

            var values = BotChatValues.Create(bot);
            values["event"] = culture => definition.Name.GetTranslation(culture) ?? string.Empty;
            RunLater(bot.GameContext, BotChatTiming.TypingDelay(line.Text), () => BotChatSpeaker.PartySayAsync(bot, party, line.Text, values, gesture: line.Gesture));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        // Same cases as the revenge (BotRevengePlugIn): a human player's kill outside of an event,
        // where dying is part of the game.
        if (this.Configuration is not { KilledByPlayerChancePercent: > 0 } configuration
            || killed is not OfflinePlayer bot
            || !BotChatAudience.IsBot(bot)
            || bot.CurrentMiniGame is not null
            || killer is not Player killerPlayer
            || killerPlayer is OfflinePlayer
            || !Rand.NextRandomBool(configuration.KilledByPlayerChancePercent)
            || !this.GetThrottle(bot.GameContext).TryReserveChat(bot, DateTime.UtcNow, configuration.BotChatCooldown))
        {
            return ValueTask.CompletedTask;
        }

        RunLater(bot.GameContext, BotChatTiming.RandomDelay(BotChatTiming.KilledDelaySeconds), () =>
        {
            // Read after the delay: the revenge plugin registers the death in the same notification.
            var lines = bot.GetRecentDeathCount(killerPlayer) >= OfflinePlayer.RepeatedKillThreshold ? configuration.KilledAgainLines : configuration.KilledLines;
            if (!this.MayTalkUnasked(bot, configuration)
                || BotChatReplySelector.PickLineFor(this.GetThrottle(bot.GameContext), killerPlayer, lines) is not { } line)
            {
                return ValueTask.CompletedTask;
            }

            var values = BotChatValues.Create(bot);
            var killerName = killerPlayer.SelectedCharacter?.Name ?? string.Empty;
            values["player"] = _ => killerName;
            return BotChatSpeaker.SayAsync(bot, line.Text, values, gesture: line.Gesture, toward: killerPlayer);
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ChatMessageSentAsync(Player sender, string message, ChatMessageType messageType, Player? receiver)
    {
        if (this.Configuration is { BusyChatMessageCount: > 0 } busyConfiguration
            && messageType == ChatMessageType.Normal
            && sender is not OfflinePlayer
            && sender.CurrentMap is { } senderMap)
        {
            this.GetThrottle(sender.GameContext).RecordHumanChat(senderMap, sender.Position, DateTime.UtcNow, busyConfiguration.BusyChatWindow);
        }

        if (this.Configuration is not { WhisperReplyChancePercent: > 0 } configuration
            || messageType is not (ChatMessageType.Normal or ChatMessageType.Party)
            || sender is OfflinePlayer
            || sender.SelectedCharacter?.Name is not { } senderName)
        {
            return;
        }

        if (messageType == ChatMessageType.Party)
        {
            this.AnswerInParty(sender, senderName, message, configuration);
            return;
        }

        // Only a bot which sees the speaker (and so got the message) may feel addressed.
        var mentionedBots = new List<Player>();
        await sender.ForEachObservingAsync<Player>(
            observer =>
            {
                if (BotChatAudience.IsBot(observer) && observer.SelectedCharacter?.Name is { } botName && BotChatComposer.MentionsName(message, botName))
                {
                    mentionedBots.Add(observer);
                }

                return ValueTask.CompletedTask;
            },
            false).ConfigureAwait(false);

        if (mentionedBots.Count == 0)
        {
            this.AnswerChimingIn(sender, senderName, message, configuration);
            return;
        }

        if (mentionedBots.FirstOrDefault(BotChatAudience.IsAvailable) is not { } bot
            || !Rand.NextRandomBool(configuration.WhisperReplyChancePercent)
            || (BotChatReplySelector.PickMerchantReplyFor(bot, this.GetThrottle(bot.GameContext), sender, message, configuration)
                ?? BotChatReplySelector.PickReplyFor(this.GetThrottle(bot.GameContext), sender, configuration.WhisperReplies, message)) is not { } line
            || !this.GetThrottle(bot.GameContext).TryReserveWhisperReply(bot, DateTime.UtcNow, configuration.WhisperReplyCooldown))
        {
            return;
        }

        var text = GetAnswerText(bot, line.Text, configuration);
        var values = BotChatValues.Create(bot);
        values["player"] = _ => senderName;
        var language = BotChatComposer.GetLanguage(message, configuration);
        RunLater(
            bot.GameContext,
            BotChatTiming.TypingDelay(text),
            () => BotChatAudience.IsAvailable(bot) ? BotChatSpeaker.SayAsync(bot, text, values, language, line.Gesture, sender) : ValueTask.CompletedTask);
    }

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        if (this.Configuration is not { } configuration)
        {
            return;
        }

        await this.MakeMerchantTalkAsync(gameContext, configuration).ConfigureAwait(false);
        if (this.GetThrottle(gameContext).TryStartPlayerScan(DateTime.UtcNow))
        {
            var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
            await this.HoldConversationsAsync(gameContext, configuration, players).ConfigureAwait(false);
            this.CongratulateResets(players, configuration);
        }
    }

    /// <inheritdoc />
    public void ForceStart() => Interlocked.Increment(ref this._forceStartCount);

    /// <inheritdoc />
    public ValueTask TradeRequestedAsync(Player requester, Player partner)
    {
        // The bot has no trade window, so the request goes nowhere - the bot says why.
        if (this.Configuration is { } configuration
            && BotChatAudience.IsBot(partner)
            && requester is not OfflinePlayer)
        {
            this.DeclineLater(partner, requester, configuration, configuration.TradeDeclineLines, null);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DuelRequestedAsync(Player requester, Player target)
    {
        // A bot never answered a duel request, which left the challenger waiting in vain: it refuses,
        // like a player clicking "no", and says why.
        if (this.Configuration is { } configuration
            && BotChatAudience.IsBot(target)
            && requester is not OfflinePlayer)
        {
            this.DeclineLater(target, requester, configuration, configuration.DuelDeclineLines, () => RefuseDuelAsync(target, requester));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MonsterItemDroppedAsync(AttackableNpcBase monster, Player killer, DroppedItem droppedItem)
    {
        // A rare drop - the nearest bot sees it fall and says so.
        if (this.Configuration is { RareDropChancePercent: > 0 } configuration
            && killer is not OfflinePlayer
            && killer.SelectedCharacter?.Name is { } killerName
            && (droppedItem.Item.IsExcellent() || droppedItem.Item.IsAncient())
            && Rand.NextRandomBool(configuration.RareDropChancePercent))
        {
            var itemName = droppedItem.Item.Definition?.Name;
            this.SayToPlayerLater(killer, configuration, configuration.RareDropLines, $"rare drop {killerName}", values => values["item"] = culture => itemName?.GetTranslation(culture) ?? string.Empty);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ObjectAddedToMapAsync(GameMap map, ILocateable addedObject)
    {
        // Called for every object added to a map - so the cheap checks come first.
        if (addedObject is not Monster monster
            || this.Configuration is not { NotableMonsterChancePercent: > 0 } configuration
            || !this.IsNotableMonster(monster.Definition.Number, configuration))
        {
            return;
        }

        var bot = map.GetAttackablesInRange(monster.Position, (int)BotChatAudience.HearingRange)
            .OfType<Player>()
            .Where(p => BotChatAudience.IsBot(p) && BotChatAudience.IsAvailable(p))
            .OrderBy(p => p.GetDistanceTo(monster))
            .FirstOrDefault();
        if (bot is null || !await BotChatAudience.HasHumanObserverAsync(bot).ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var throttle = this.GetThrottle(bot.GameContext);
        if (!this.MayTalkUnasked(bot, configuration)
            || !throttle.TryReserveOccasion($"notable monster {map.MapId} {monster.Definition.Number}", now, NotableMonsterAnnouncementWindow)
            || !Rand.NextRandomBool(configuration.NotableMonsterChancePercent)
            || BotChatComposer.PickCurrentLine(configuration.NotableMonsterLines) is not { } line
            || !throttle.TryReserveChat(bot, now, configuration.BotChatCooldown))
        {
            return;
        }

        var values = BotChatValues.Create(bot);
        var monsterName = monster.Definition.Designation;
        values["monster"] = culture => monsterName.GetTranslation(culture) ?? string.Empty;
        RunLater(bot.GameContext, BotChatTiming.RandomDelay(BotChatTiming.NotableMonsterDelaySeconds), () => BotChatAudience.IsAvailable(bot) ? BotChatSpeaker.SayAsync(bot, line.Text, values, gesture: line.Gesture) : ValueTask.CompletedTask);
    }

    /// <inheritdoc />
    public void BotEarnedWings(Player bot, Item wings)
    {
        if (this.Configuration is not { WingsChancePercent: > 0 } configuration
            || !BotChatAudience.IsBot(bot)
            || !Rand.NextRandomBool(configuration.WingsChancePercent)
            || BotChatComposer.PickCurrentLine(configuration.OwnWingsLines) is not { } line)
        {
            return;
        }

        RunLater(bot.GameContext, BotChatTiming.RandomDelay(BotChatTiming.LevelMilestoneDelaySeconds), async () =>
        {
            // New wings are something to show off - to nobody, it would be wasted.
            if (!BotChatAudience.IsAvailable(bot)
                || !this.MayTalkUnasked(bot, configuration)
                || !await BotChatAudience.HasHumanObserverAsync(bot).ConfigureAwait(false)
                || !this.GetThrottle(bot.GameContext).TryReserveChat(bot, DateTime.UtcNow, configuration.BotChatCooldown))
            {
                return;
            }

            await BotChatSpeaker.SayAsync(bot, line.Text, BotChatValues.Create(bot), gesture: line.Gesture).ConfigureAwait(false);
        });
    }

    /// <inheritdoc />
    public ValueTask ItemMovedAsync(Player player, Item item)
    {
        // Wings go on again after every change of equipment, so each pair of a player gets one comment
        // at most - remembered before the dice, or re-equipping would just roll again.
        if (this.Configuration is { WingsChancePercent: > 0 } configuration
            && player is not OfflinePlayer
            && ReferenceEquals(BotChatValues.GetWings(player), item)
            && item.Definition is { } definition
            && player.SelectedCharacter?.Name is { } playerName
            && this.GetThrottle(player.GameContext).TryRememberWings(playerName, string.Create(CultureInfo.InvariantCulture, $"{definition.Group}/{definition.Number}+{item.Level}"))
            && Rand.NextRandomBool(configuration.WingsChancePercent))
        {
            var wingsText = BotChatValues.GetWingsText(item);
            this.SayToPlayerLater(player, configuration, configuration.PlayerWingsLines, $"wings {playerName}", values => values["wings"] = wingsText);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask PostMessageSentAsync(Player sender, string message)
    {
        if (this.Configuration is not { PartyVolunteerCount: > 0 } configuration
            || sender is OfflinePlayer
            || sender.SelectedCharacter?.Name is not { } senderName
            || BotPartyRequest.TryParse(
                message,
                configuration.PartyRequestKeywords,
                configuration.PartyClassAliases,
                GetPartyEventKeywords(configuration),
                sender.GameContext.Configuration.MiniGameDefinitions) is not { } request)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var throttle = this.GetThrottle(sender.GameContext);
        var classes = sender.GameContext.Configuration.CharacterClasses;
        var players = await sender.GameContext.GetPlayersAsync().ConfigureAwait(false);
        var candidates = players
            .OfType<OfflinePlayer>()
            .Where(bot => BotChatAudience.IsBot(bot)
                          && BotChatAudience.IsAvailable(bot)
                          && BotChatAudience.IsFreeToMeet(bot)
                          && throttle.GetOpenPartyOffer(bot, now) is null
                          && request.IsWantedLevel(BotChatAudience.GetLevel(bot))
                          && BotPartyRequest.IsOfWantedClass(bot, request, classes)
                          && (request.MiniGame is null || BotMiniGameHandler.IsEligible(bot, request.MiniGame, out _)));
        var volunteers = throttle.TakeNewVolunteers(sender, candidates, configuration.PartyVolunteerCount, now, count => Rand.NextInt(0, count));
        var delay = TimeSpan.Zero;
        foreach (var bot in volunteers)
        {
            if (BotChatComposer.PickCurrentLine(configuration.PartyVolunteerLines) is not { } line)
            {
                return;
            }

            if (!throttle.TryReservePartyOffer(bot, new BotPartyOffer(sender, now + configuration.PartyOfferTimeout), now))
            {
                // Taken by another player's search since the candidates were picked.
                continue;
            }

            delay += BotChatTiming.RandomDelay(BotChatTiming.PartyVolunteerSpacingSeconds);
            var whisperDelay = delay + BotChatTiming.TypingDelay(line.Text);
            var values = BotChatValues.Create(bot);
            values["player"] = _ => senderName;
            RunLater(
                bot.GameContext,
                whisperDelay,
                () => BotChatAudience.IsAvailable(bot) && sender.PlayerState.CurrentState == PlayerState.EnteredWorld
                    ? BotChatSpeaker.WhisperAsync(bot, sender, line.Text, values, gesture: line.Gesture)
                    : ValueTask.CompletedTask);
        }
    }

    /// <summary>
    /// Runs the action after the delay, without holding up the caller - the plugin points are called
    /// from the game logic, which must not wait for a bot's "typing time". A plugin deactivated
    /// meanwhile drops the action: the bots fall silent at once, not a few lines later.
    /// </summary>
    /// <param name="gameContext">The game context the action runs in.</param>
    /// <param name="delay">How long to wait before running the action.</param>
    /// <param name="action">The action to run.</param>
    internal static void RunLater(IGameContext gameContext, TimeSpan delay, Func<ValueTask> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (!gameContext.PlugInManager.IsPlugInActive(typeof(BotChatPlugIn)))
                {
                    return;
                }

                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                gameContext.LoggerFactory.CreateLogger<BotChatPlugIn>().LogError(ex, "Bot chat failed.");
            }
        });
    }

    /// <summary>
    /// Gets the chat limits and memory of the bots of a game server.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <returns>The throttle.</returns>
    internal BotChatThrottle GetThrottle(IGameContext gameContext) => this._throttles.GetOrCreateValue(gameContext);

    private static IEnumerable<(MiniGameType Type, string Keywords)> GetPartyEventKeywords(BotChatConfiguration configuration) =>
    [
        (MiniGameType.BloodCastle, configuration.PartyBloodCastleKeywords),
        (MiniGameType.DevilSquare, configuration.PartyDevilSquareKeywords),
        (MiniGameType.ChaosCastle, configuration.PartyChaosCastleKeywords),
    ];

    /// <summary>
    /// Gets the text a bot answers with: the chosen reply - unless it names the bot's wings and the
    /// bot has none, then one of the <see cref="BotChatConfiguration.WithoutWingsLines"/>.
    /// </summary>
    private static LocalizedString GetAnswerText(Player bot, LocalizedString text, BotChatConfiguration configuration)
        => text.ValueInNeutralLanguage.Contains("{wings}", StringComparison.Ordinal)
           && BotChatValues.GetWings(bot) is null
           && BotChatComposer.PickCurrentLine(configuration.WithoutWingsLines) is { } withoutWings
            ? withoutWings.Text
            : text;

    /// <summary>
    /// Refuses the duel the player challenged the bot to - unless the challenge is already over, e.g.
    /// the player took it back meanwhile.
    /// </summary>
    private static ValueTask RefuseDuelAsync(Player bot, Player requester)
        => bot.DuelRoom is { State: DuelState.DuelRequested } duelRoom && ReferenceEquals(duelRoom.Requester, requester)
            ? new DuelActions().HandleDuelResponseAsync(bot, requester, false)
            : ValueTask.CompletedTask;

    /// <summary>
    /// Determines whether the bot may say something nobody asked for in the normal chat: not while
    /// the human players around it are busy talking - the bots shouldn't drown them out.
    /// </summary>
    private bool MayTalkUnasked(Player bot, BotChatConfiguration configuration)
        => bot.CurrentMap is not { } map
           || !this.GetThrottle(bot.GameContext).IsHumanChatBusy(map, bot.Position, BotChatAudience.HearingRange, DateTime.UtcNow, configuration.BusyChatWindow, configuration.BusyChatMessageCount);

    /// <summary>
    /// Answers the whisper in which a player, whose party search the bot answered, names the meeting
    /// place: "devias 215 40", or "here" for where the player stands. A bot which may go there sets
    /// off and waits for the invitation (see <see cref="BotEventMeeting"/>); a place it may not go to
    /// is declined, and a whisper without a place gets asked again.
    /// </summary>
    /// <summary>
    /// Answers a player's message in the party chat: the bot of the party whose name the message
    /// mentions answers like to a whisper; without a name, one bot of the party answers only a
    /// message which matches a keyword ("what level are you?") - not every "ok" or "let's go".
    /// </summary>
    private void AnswerInParty(Player sender, string senderName, string message, BotChatConfiguration configuration)
    {
        if (sender.Party is not { } party)
        {
            return;
        }

        // Also a bot inside an event with the party may answer - the party chat reaches it there.
        var bots = party.PartyList
            .OfType<Player>()
            .Where(p => BotChatAudience.IsBot(p) && p.PlayerState.CurrentState == PlayerState.EnteredWorld)
            .ToList();
        var mentioned = bots.FirstOrDefault(bot => bot.SelectedCharacter?.Name is { } botName && BotChatComposer.MentionsName(message, botName));
        var speaker = mentioned ?? (bots.Count > 0 ? bots[Rand.NextInt(0, bots.Count)] : null);
        if (speaker is null
            || !Rand.NextRandomBool(configuration.WhisperReplyChancePercent)
            || BotChatReplySelector.PickReplyFor(this.GetThrottle(speaker.GameContext), sender, configuration.WhisperReplies, message, requireKeyword: mentioned is null) is not { } line
            || !this.GetThrottle(speaker.GameContext).TryReserveWhisperReply(speaker, DateTime.UtcNow, configuration.WhisperReplyCooldown))
        {
            return;
        }

        var text = GetAnswerText(speaker, line.Text, configuration);
        var values = BotChatValues.Create(speaker);
        values["player"] = _ => senderName;
        var language = BotChatComposer.GetLanguage(message, configuration);
        RunLater(speaker.GameContext, BotChatTiming.TypingDelay(text), () => BotChatSpeaker.PartySayAsync(speaker, party, text, values, language, line.Gesture, sender));
    }

    /// <summary>
    /// Answers a player who chimes in right after two bots near them had a conversation: one of the
    /// two answers - by a keyword reply if the message has one, otherwise with one of the
    /// <see cref="BotChatConfiguration.ConversationJoinLines"/> ("right?", "haha, exactly").
    /// </summary>
    private void AnswerChimingIn(Player sender, string senderName, string message, BotChatConfiguration configuration)
    {
        if (this.GetThrottle(sender.GameContext).TakeRecentConversation(bot => BotChatAudience.IsInHearingRange(bot, sender), DateTime.UtcNow) is not { } conversation
            || !Rand.NextRandomBool(configuration.WhisperReplyChancePercent))
        {
            return;
        }

        var bot = new[] { conversation.First, conversation.Second }.Where(b => BotChatAudience.IsAvailable(b) && BotChatAudience.IsInHearingRange(b, sender)).OrderBy(_ => Rand.NextInt(0, 2)).FirstOrDefault();
        var senderThrottle = this.GetThrottle(sender.GameContext);
        LocalizedString? text = BotChatReplySelector.PickReplyFor(senderThrottle, sender, configuration.WhisperReplies, message, requireKeyword: true) is { } reply
            ? reply.Text
            : BotChatReplySelector.PickLineFor(senderThrottle, sender, configuration.ConversationJoinLines)?.Text;
        if (bot is null
            || text is not { } chosen
            || !this.GetThrottle(bot.GameContext).TryReserveWhisperReply(bot, DateTime.UtcNow, configuration.WhisperReplyCooldown))
        {
            return;
        }

        var answer = GetAnswerText(bot, chosen, configuration);

        var values = BotChatValues.Create(bot);
        values["player"] = _ => senderName;
        var language = BotChatComposer.GetLanguage(message, configuration);
        RunLater(bot.GameContext, BotChatTiming.TypingDelay(answer, (bot as OfflinePlayer)?.SessionRandom), () => BotChatAudience.IsAvailable(bot) ? BotChatSpeaker.SayAsync(bot, answer, values, language, toward: sender) : ValueTask.CompletedTask);
    }

    /// <summary>
    /// Declines a player's request after a human-like moment: runs the refusal, if any, and whispers
    /// one of the lines - by chance, and not more often than the whisper reply cooldown allows.
    /// </summary>
    private void DeclineLater(Player bot, Player requester, BotChatConfiguration configuration, ICollection<BotChatLine> lines, Func<ValueTask>? refuse)
    {
        var whisper = configuration.DeclineReplyChancePercent > 0
                      && Rand.NextRandomBool(configuration.DeclineReplyChancePercent)
                      && this.GetThrottle(bot.GameContext).TryReserveWhisperReply(bot, DateTime.UtcNow, configuration.WhisperReplyCooldown);
        var line = whisper ? BotChatReplySelector.PickLineFor(this.GetThrottle(requester.GameContext), requester, lines) : null;
        var values = BotChatValues.Create(bot);
        var requesterName = requester.SelectedCharacter?.Name ?? string.Empty;
        values["player"] = _ => requesterName;
        RunLater(bot.GameContext, line is null ? BotChatTiming.RandomDelay(BotChatTiming.TypingReactionSeconds, (bot as OfflinePlayer)?.SessionRandom) : BotChatTiming.TypingDelay(line.Text, (bot as OfflinePlayer)?.SessionRandom), async () =>
        {
            if (refuse is not null)
            {
                await refuse().ConfigureAwait(false);
            }

            if (line is not null && requester.PlayerState.CurrentState == PlayerState.EnteredWorld)
            {
                await BotChatSpeaker.WhisperAsync(bot, requester, line.Text, values, gesture: line.Gesture).ConfigureAwait(false);
            }
        });
    }

    private void WhisperLater(Player bot, Player receiver, BotChatLine line, Dictionary<string, Func<CultureInfo, string>> values, CultureInfo? language = null)
        => RunLater(
            bot.GameContext,
            BotChatTiming.TypingDelay(line.Text),
            () => receiver.PlayerState.CurrentState == PlayerState.EnteredWorld
                ? BotChatSpeaker.WhisperAsync(bot, receiver, line.Text, values, language, line.Gesture)
                : ValueTask.CompletedTask);

    private async ValueTask MakeMerchantTalkAsync(GameContext gameContext, BotChatConfiguration configuration)
    {
        if (configuration.MerchantTalkChancePercent <= 0
            || !this.GetThrottle(gameContext).TryStartMerchantTalk(DateTime.UtcNow, configuration.MerchantTalkInterval, Volatile.Read(ref this._forceStartCount))
            || !Rand.NextRandomBool(configuration.MerchantTalkChancePercent))
        {
            return;
        }

        var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
        var candidates = new List<Player>();

        // Only a bot which really sells something advertises: one behind its open store on the market
        // (see BotMerchantPlugIn).
        foreach (var bot in players.Where(p => BotChatAudience.IsBot(p) && BotChatAudience.IsAvailable(p) && p is OfflinePlayer { MarketShift: { IsStoreOpened: true, IsEnding: false } }))
        {
            // Talking to nobody would be wasted: only a bot which a player sees may talk.
            if (await BotChatAudience.HasHumanObserverAsync(bot).ConfigureAwait(false))
            {
                candidates.Add(bot);
            }
        }

        if (candidates.Count == 0
            || BotChatComposer.PickCurrentLine(configuration.MerchantLines) is not { } line)
        {
            return;
        }

        var speaker = candidates[Rand.NextInt(0, candidates.Count)];
        if (this.MayTalkUnasked(speaker, configuration)
            && this.GetThrottle(gameContext).TryReserveChat(speaker, DateTime.UtcNow, configuration.BotChatCooldown))
        {
            await BotChatSpeaker.SayAsync(speaker, line.Text, BotChatValues.Create(speaker), gesture: line.Gesture).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lets two bots near a human player talk, for every player whose interval is over: the pair
    /// stands close together - both in a town or both out hunting - and the conversation fits the
    /// place. Every player who hears it waits a whole interval for the next one.
    /// </summary>
    private async ValueTask HoldConversationsAsync(GameContext gameContext, BotChatConfiguration configuration, IList<Player> players)
    {
        var now = DateTime.UtcNow;
        var throttle = this.GetThrottle(gameContext);
        if (configuration.ConversationChancePercent <= 0
            || configuration.Conversations.Count == 0)
        {
            return;
        }

        var humans = players.Where(p => p is not OfflinePlayer && p.PlayerState.CurrentState == PlayerState.EnteredWorld && p.CurrentMap is not null).ToList();
        var bots = players.Where(p => BotChatAudience.IsBot(p) && BotChatAudience.IsAvailable(p)).ToList();
        foreach (var human in humans)
        {
            if (!throttle.IsConversationDue(human, now, configuration.ConversationInterval)
                || BotChatAudience.FindConversationPair(bots.Where(bot => BotChatAudience.IsInHearingRange(bot, human)).ToList()) is not { } pair)
            {
                continue;
            }

            // Whoever hears this one waits a whole interval - also when the dice said no.
            foreach (var listener in humans.Where(listener => BotChatAudience.IsInHearingRange(pair.First, listener) || BotChatAudience.IsInHearingRange(pair.Second, listener)))
            {
                throttle.PostponeConversation(listener, now + configuration.ConversationInterval);
            }

            var timeOfDay = BotChatTiming.GetTimeOfDay(DateTime.Now.Hour);
            var candidates = configuration.Conversations
                .Where(c => (c.Where == BotConversationPlace.Anywhere || c.Where == pair.Place)
                            && BotChatComposer.FitsTimeOfDay(c.When, timeOfDay)
                            && !string.IsNullOrWhiteSpace(c.Opener.ValueInNeutralLanguage)
                            && !string.IsNullOrWhiteSpace(c.Reply.ValueInNeutralLanguage))
                .ToList();
            if (!Rand.NextRandomBool(configuration.ConversationChancePercent)
                || !this.MayTalkUnasked(pair.First, configuration)
                || !throttle.TryReserveChat(pair.First, now, configuration.BotChatCooldown)
                || !throttle.TryReserveChat(pair.Second, now, configuration.BotChatCooldown)
                || throttle.DrawConversation(candidates, count => Rand.NextInt(0, count)) is not { } conversation)
            {
                continue;
            }

            var (first, second) = (pair.First, pair.Second);
            var firstValues = BotChatValues.Create(first);
            var secondName = second.SelectedCharacter?.Name ?? string.Empty;
            firstValues["partner"] = _ => secondName;
            var secondValues = BotChatValues.Create(second);
            var firstName = first.SelectedCharacter?.Name ?? string.Empty;
            secondValues["partner"] = _ => firstName;

            await BotChatSpeaker.SayAsync(first, conversation.Opener, firstValues).ConfigureAwait(false);
            var replyDelay = BotChatTiming.TypingDelay(conversation.Reply);
            var hasRiposte = !string.IsNullOrWhiteSpace(conversation.Riposte.ValueInNeutralLanguage);
            var riposteDelay = hasRiposte ? replyDelay + BotChatTiming.TypingDelay(conversation.Riposte) : replyDelay;
            RunLater(
                gameContext,
                replyDelay,
                () => BotChatAudience.IsAvailable(second) ? BotChatSpeaker.SayAsync(second, conversation.Reply, secondValues) : ValueTask.CompletedTask);
            if (hasRiposte)
            {
                RunLater(
                    gameContext,
                    riposteDelay,
                    () => BotChatAudience.IsAvailable(first) ? BotChatSpeaker.SayAsync(first, conversation.Riposte, firstValues) : ValueTask.CompletedTask);
            }

            throttle.RememberConversation(first, second, now + riposteDelay + ConversationChimeInWindow, now);
        }
    }

    /// <summary>
    /// Lets the nearest bot congratulate the players whose reset count grew since the last look - the
    /// reset may log the player out to the character selection or not, so it's noticed by the count.
    /// </summary>
    private void CongratulateResets(IEnumerable<Player> players, BotChatConfiguration configuration)
    {
        foreach (var player in players)
        {
            if (player is OfflinePlayer
                || player.SelectedCharacter?.Name is not { } name
                || player.Attributes is not { } attributes)
            {
                continue;
            }

            var resets = (int)attributes[Stats.Resets];
            if (this.GetThrottle(player.GameContext).TryUpdateKnownResets(name, resets))
            {
                this.Congratulate(player, configuration, configuration.ResetCongratulationLines, $"reset congratulation {name}");
            }
        }
    }

    /// <summary>
    /// Lets the nearest bot congratulate a player in the normal chat - "gz" - after a moment, at most
    /// once per player and cooldown for each kind of occasion.
    /// </summary>
    private void Congratulate(Player player, BotChatConfiguration configuration, ICollection<BotChatLine> lines, string occasionKey)
    {
        if (configuration.CongratulationChancePercent <= 0
            || !Rand.NextRandomBool(configuration.CongratulationChancePercent))
        {
            return;
        }

        var playerLevel = BotChatAudience.GetLevel(player).ToString(CultureInfo.InvariantCulture);
        var playerResets = ((int)(player.Attributes?[Stats.Resets] ?? 0)).ToString(CultureInfo.InvariantCulture);
        this.SayToPlayerLater(player, configuration, lines, occasionKey, values =>
        {
            values["level"] = _ => playerLevel;
            values["resets"] = _ => playerResets;
        });
    }

    /// <summary>
    /// Lets the nearest bot which sees the player say one of the lines to them in the normal chat,
    /// after a moment - at most once per occasion and the congratulation cooldown.
    /// </summary>
    private void SayToPlayerLater(Player player, BotChatConfiguration configuration, ICollection<BotChatLine> lines, string occasionKey, Action<Dictionary<string, Func<CultureInfo, string>>> addValues)
    {
        if (player.SelectedCharacter?.Name is not { } playerName)
        {
            return;
        }

        RunLater(player.GameContext, BotChatTiming.RandomDelay(BotChatTiming.CongratulationDelaySeconds), async () =>
        {
            if (player.PlayerState.CurrentState != PlayerState.EnteredWorld || player.CurrentMap is not { } map)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var throttle = this.GetThrottle(player.GameContext);
            var players = await player.GameContext.GetPlayersAsync().ConfigureAwait(false);
            if (BotChatAudience.FindGreeter(players, player, map) is not { } bot
                || !this.MayTalkUnasked(bot, configuration)
                || BotChatReplySelector.PickLineFor(throttle, player, lines) is not { } line
                || !throttle.TryReserveOccasion(occasionKey, now, configuration.CongratulationCooldown)
                || !throttle.TryReserveChat(bot, now, configuration.BotChatCooldown))
            {
                return;
            }

            var values = BotChatValues.Create(bot);
            values["player"] = _ => playerName;
            addValues(values);
            await BotChatSpeaker.SayAsync(bot, line.Text, values, gesture: line.Gesture, toward: player).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Determines whether the monster is one of the configured notable monsters - parsed once per
    /// configured text, since the check runs for every monster spawn.
    /// </summary>
    private bool IsNotableMonster(short number, BotChatConfiguration configuration)
    {
        var text = configuration.NotableMonsterNumbers ?? string.Empty;
        var cache = this._notableMonsters;
        if (!ReferenceEquals(cache.Text, text))
        {
            var numbers = text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => short.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (short)-1)
                .Where(n => n >= 0)
                .ToHashSet();
            cache = (text, numbers);
            this._notableMonsters = cache;
        }

        return cache.Numbers.Contains(number);
    }

    private async ValueTask GreetAsync(Player player, BotChatConfiguration configuration)
    {
        if (player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || player.CurrentMap is not { } map
            || player.SelectedCharacter?.Name is not { } playerName)
        {
            return;
        }

        var players = await player.GameContext.GetPlayersAsync().ConfigureAwait(false);
        var throttle = this.GetThrottle(player.GameContext);

        // An old party companion nearby greets first.
        var bot = BotChatAudience.FindGreeter(players.Where(p => throttle.IsFriend(p, playerName)), player, map) ?? BotChatAudience.FindGreeter(players, player, map);
        var now = DateTime.UtcNow;
        if (bot is null
            || !this.MayTalkUnasked(bot, configuration)
            || BotChatReplySelector.PickLineFor(throttle, player, throttle.IsFriend(bot, playerName) && configuration.FriendLines.Count > 0 ? configuration.FriendLines : configuration.GreetingLines) is not { } line
            || !throttle.TryReserveOccasion($"greeting {playerName}", now, configuration.GreetingCooldown)
            || !throttle.TryReserveChat(bot, now, configuration.BotChatCooldown))
        {
            return;
        }

        var values = BotChatValues.Create(bot);
        values["player"] = _ => playerName;
        await BotChatSpeaker.SayAsync(bot, line.Text, values, gesture: line.Gesture, toward: player).ConfigureAwait(false);
    }
}
