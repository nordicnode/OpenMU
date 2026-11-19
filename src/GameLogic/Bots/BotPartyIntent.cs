// <copyright file="BotPartyIntent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// What a bot wants from a party right now: the goal and, for goals with a destination, the place and
/// time it is about. Bots with the same intent are candidates for the same group; a bot without a
/// special intent grinds experience.
/// </summary>
/// <param name="Goal">The goal of the party.</param>
/// <param name="Map">The destination of an event run, boss attempt or escort; <c>null</c> for experience.</param>
/// <param name="UntilUtc">When the destination stops being relevant; <c>null</c> for experience.</param>
/// <param name="MiniGame">The event of an event run, which carries the entry rules; <c>null</c> for other goals.</param>
internal sealed record BotPartyIntent(
    BotPartyGoal Goal,
    GameMapDefinition? Map = null,
    DateTime? UntilUtc = null,
    MiniGameDefinition? MiniGame = null)
{
    /// <summary>
    /// Gets the intent of a bot which just wants to hunt.
    /// </summary>
    internal static BotPartyIntent Experience { get; } = new(BotPartyGoal.Experience);

    /// <summary>
    /// Gets the identity of the destination, so bots heading to different places do not share a group.
    /// </summary>
    internal (BotPartyGoal Goal, short? Map, MiniGameType? Type, byte? Level) Destination
        => (this.Goal, this.Map?.Number, this.MiniGame?.Type, this.MiniGame?.GameLevel);
}
