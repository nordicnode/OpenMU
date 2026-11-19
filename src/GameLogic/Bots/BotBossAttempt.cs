// <copyright file="BotBossAttempt.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// An invasion which a bot wants to join (see <see cref="BotChatEventAnnouncer"/>). It is a
/// goal-driven party target: the bot travels to the invaded map, groups with other bots which are
/// ready for it, and hunts the event's monsters until the attempt expires.
/// </summary>
/// <param name="Map">The invaded map to travel to.</param>
/// <param name="UntilUtc">When the attempt stops being interesting.</param>
internal sealed record BotBossAttempt(GameMapDefinition Map, DateTime UntilUtc);
