// <copyright file="BotAccount.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Shared.Services;

using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Keeps the displayed data of an active server-side bot.
/// </summary>
/// <param name="LoginName">The account login name the bot is driving.</param>
/// <param name="ServerId">The server identifier.</param>
/// <param name="CharacterName">The character which the bot is driving.</param>
/// <param name="StartedAt">The start timestamp of the bot session.</param>
/// <param name="GuildName">The guild of the character, if any.</param>
/// <param name="PartyMaster">The character name of the party master, if the bot is in a party.</param>
/// <param name="PartySize">The number of party members, if the bot is in a party.</param>
/// <param name="State">What the bot is doing right now in one line, for finding a bot which is wedged, idle or stuck on an errand; <c>null</c> while it isn't navigating.</param>
/// <param name="StateDetail">The detailed state description, including where the bot's time went and the transitions it took before.</param>
/// <param name="SecondsInState">How long the bot has already been in its current mode, which is what the table sorts by.</param>
/// <param name="Restarts">How many times the account was restarted to heal the bot, which is what tells a bot with a
/// passing hiccup from one which breaks itself again and again.</param>
/// <param name="QuarantinedSeconds">For how many seconds the bot is held back from another restart, or <c>0</c>
/// while it may be restarted.</param>
/// <param name="Meeting">The event meeting the bot is on, with its state, place and remaining wait time, or
/// <c>null</c> while it has none.</param>
/// <param name="LastMeeting">How the last event meeting ended, with its state, place, event and reason, or
/// <c>null</c> while the bot never had one.</param>
/// <param name="LootDecisions">The most recent loot decisions of the bot, newest first - why it kept,
/// equipped, sold or stored an item - or <c>null</c> while it has none.</param>
public record BotAccount(
    string LoginName,
    byte ServerId,
    string? CharacterName,
    DateTime StartedAt,
    string? GuildName = null,
    string? PartyMaster = null,
    int PartySize = 0,
    string? State = null,
    string? StateDetail = null,
    double SecondsInState = 0,
    int Restarts = 0,
    double QuarantinedSeconds = 0,
    BotMeetingActivity? Meeting = null,
    BotMeetingOutcome? LastMeeting = null,
    IReadOnlyList<BotLootDecisionInfo>? LootDecisions = null) : IPartyGroupedAccount;
