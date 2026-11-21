// <copyright file="LiveBotHealthReset.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>What a bot health reset cleared for one bot of an account.</summary>
/// <param name="Character">The character name, if the bot was live on the resetting server.</param>
/// <param name="CharacterSlot">The character slot, if it is known.</param>
/// <param name="Restarts">The forgotten restart count.</param>
/// <param name="Faults">The forgotten fault count.</param>
/// <param name="QuarantinedSeconds">The quarantine which was lifted, in seconds.</param>
public sealed record LiveBotHealthReset(string? Character, int? CharacterSlot, int Restarts, int Faults, int QuarantinedSeconds);
