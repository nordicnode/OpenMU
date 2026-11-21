// <copyright file="LiveBotInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>A live bot's status and health, projected by its owning game server.</summary>
/// <param name="Account">The account login name.</param>
/// <param name="Character">The character name.</param>
/// <param name="Level">The character level.</param>
/// <param name="Map">The current map number.</param>
/// <param name="StartedAtUtc">The session start time.</param>
/// <param name="GuildId">The live guild identifier.</param>
/// <param name="PartyMaster">The party master's name.</param>
/// <param name="PartySize">The number of party members.</param>
/// <param name="State">The activity state.</param>
/// <param name="Reason">The activity reason.</param>
/// <param name="SecondsInState">The time spent in the current state.</param>
/// <param name="Summary">The activity summary.</param>
/// <param name="Detail">The activity detail.</param>
/// <param name="Failures">The number of failed ticks.</param>
/// <param name="LastFailure">The last failure description.</param>
/// <param name="Restarts">The number of restarts.</param>
/// <param name="QuarantinedUntilUtc">The end of quarantine, if any.</param>
/// <param name="Meeting">The current meeting, if any.</param>
/// <param name="LastMeeting">The last meeting outcome, if any.</param>
/// <param name="LootDecisions">Recent loot decisions, newest first.</param>
public sealed record LiveBotInfo(
    string? Account, string? Character, int Level, ushort Map, DateTime StartedAtUtc,
    uint? GuildId, string? PartyMaster, int PartySize, string? State, string? Reason,
    double SecondsInState, string? Summary, string? Detail, int Failures, string? LastFailure,
    int Restarts, DateTimeOffset? QuarantinedUntilUtc, LiveBotMeeting? Meeting,
    LiveBotMeetingOutcome? LastMeeting, IReadOnlyList<LiveBotLootDecision> LootDecisions);
