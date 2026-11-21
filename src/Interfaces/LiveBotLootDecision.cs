// <copyright file="LiveBotLootDecision.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>A recent bot loot decision.</summary>
/// <param name="Item">The item description.</param>
/// <param name="Action">The action code.</param>
/// <param name="Reason">The reason code.</param>
/// <param name="Detail">The optional detail.</param>
/// <param name="AtUtc">The decision time.</param>
public sealed record LiveBotLootDecision(string Item, string Action, string Reason, string? Detail, DateTimeOffset AtUtc);
