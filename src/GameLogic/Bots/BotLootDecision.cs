// <copyright file="BotLootDecision.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// The explainable outcome of the loot policy for one item: what the bot does with it and why.
/// </summary>
/// <param name="Action">What the bot does with the item.</param>
/// <param name="Reason">Why it does that.</param>
/// <param name="Detail">An optional detail for the log, e.g. the price class of a market candidate.</param>
internal readonly record struct BotLootDecision(BotLootAction Action, BotLootReason Reason, string? Detail = null)
{
    /// <summary>
    /// Gets a value indicating whether the item is sold to a merchant NPC or put into the market store.
    /// Both are collected as junk; the action tells them apart.
    /// </summary>
    public bool IsJunk => this.Action is BotLootAction.Sell or BotLootAction.Store;

    /// <summary>
    /// Gets the stable, lower-case action code, e.g. <c>store</c>.
    /// </summary>
    internal string ActionCode => ToCode(this.Action);

    /// <summary>
    /// Gets the stable, lower-case reason code, e.g. <c>market_candidate</c>.
    /// </summary>
    internal string ReasonCode => ToCode(this.Reason);

    /// <summary>
    /// Gets the stable, lower-case code used in logs, e.g. <c>store/market_candidate</c>.
    /// </summary>
    public string Code => $"{this.ActionCode}/{this.ReasonCode}";

    /// <inheritdoc />
    public override string ToString()
        => this.Detail is { Length: > 0 } ? $"{this.Code} ({this.Detail})" : this.Code;

    private static string ToCode<T>(T value)
        where T : struct, Enum
        => string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));
}
