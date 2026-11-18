// <copyright file="BotAccount.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Shared.Services;

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
public record BotAccount(string LoginName, byte ServerId, string? CharacterName, DateTime StartedAt, string? GuildName = null, string? PartyMaster = null, int PartySize = 0, string? State = null, string? StateDetail = null) : IPartyGroupedAccount;
