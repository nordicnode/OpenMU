// <copyright file="BotLootDecisionInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// One recent loot decision of a bot, in the shape the administration surfaces show it. The record is
/// kept in memory only, so the bot table can explain a decision without debug logging being enabled.
/// </summary>
/// <param name="Item">The item the decision was about.</param>
/// <param name="Action">The lower-case action code, e.g. <c>store</c>.</param>
/// <param name="Reason">The lower-case reason code, e.g. <c>market_candidate</c>.</param>
/// <param name="Detail">An optional detail, e.g. the price class of a market candidate.</param>
/// <param name="AtUtc">When the decision was made.</param>
public sealed record BotLootDecisionInfo(string Item, string Action, string Reason, string? Detail, DateTimeOffset AtUtc)
{
    /// <summary>
    /// Gets the combined action and reason code, e.g. <c>store/market_candidate</c>.
    /// </summary>
    public string Code => string.IsNullOrEmpty(this.Detail)
        ? $"{this.Action}/{this.Reason}"
        : $"{this.Action}/{this.Reason} ({this.Detail})";

    /// <summary>
    /// Gets the one-line form shown in the bot table and its tooltip. The time is always UTC, like the
    /// machine-readable timestamps of the report, no matter which offset the timestamp was created with.
    /// </summary>
    public string Summary => $"{this.AtUtc.UtcDateTime:HH:mm:ss} {this.Code}: {this.Item}";

    /// <summary>
    /// Creates the administration record from a policy decision.
    /// </summary>
    /// <param name="item">The item the decision was about.</param>
    /// <param name="decision">The policy decision.</param>
    /// <param name="atUtc">When the decision was made.</param>
    /// <returns>The administration record.</returns>
    internal static BotLootDecisionInfo From(Item item, BotLootDecision decision, DateTimeOffset atUtc)
        => new(Describe(item), decision.ActionCode, decision.ReasonCode, decision.Detail, atUtc);

    private static string Describe(Item item)
    {
        var name = item.Definition?.GetNameForLevel(item.Level);
        if (string.IsNullOrEmpty(name))
        {
            return item.ToString();
        }

        return item.Level > 0 ? $"{name} +{item.Level}" : name;
    }
}
