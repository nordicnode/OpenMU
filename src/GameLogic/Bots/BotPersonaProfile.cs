// <copyright file="BotPersonaProfile.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// The bounded effect a <see cref="BotPersona"/> has on the bot's behavior. Kept to three multipliers with
/// hard limits, so a persona can never make a bot reckless or break a routine: it only shifts decisions
/// inside the ranges the bot already works in.
/// </summary>
/// <param name="Persona">The persona this profile belongs to.</param>
/// <param name="CombatTolerance">
/// Factor applied to the share of its health a single monster hit may take. Below 1 the bot accepts
/// bigger hits (aggressive), above 1 it keeps more distance (cautious).
/// </param>
/// <param name="ShoppingSlotBonus">
/// Extra free backpack slots below which the bot heads to a merchant. Positive means it shops earlier.
/// </param>
/// <param name="SocialDelayFactor">
/// Factor on the delay before answering an invitation. Below 1 answers faster, above 1 thinks longer.
/// </param>
internal sealed record BotPersonaProfile(
    BotPersona Persona,
    float CombatTolerance,
    int ShoppingSlotBonus,
    float SocialDelayFactor)
{
    /// <summary>The default persona mix used when a server does not configure one.</summary>
    internal const string DefaultMix = "balanced:70,aggressive:10,cautious:10,trader:5,social:5";

    private static readonly BotPersonaProfile[] Profiles =
    [
        new(BotPersona.Balanced, CombatTolerance: 1f, ShoppingSlotBonus: 0, SocialDelayFactor: 1f),
        new(BotPersona.Aggressive, CombatTolerance: 0.85f, ShoppingSlotBonus: 0, SocialDelayFactor: 0.9f),
        new(BotPersona.Cautious, CombatTolerance: 1.25f, ShoppingSlotBonus: 4, SocialDelayFactor: 1.15f),
        new(BotPersona.Trader, CombatTolerance: 1f, ShoppingSlotBonus: 10, SocialDelayFactor: 1f),
        new(BotPersona.Social, CombatTolerance: 1f, ShoppingSlotBonus: 0, SocialDelayFactor: 0.55f),
    ];

    /// <summary>
    /// The profile of the given player: its assigned persona for a bot, the balanced default for a human
    /// player in an offline session.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The profile.</returns>
    internal static BotPersonaProfile For(Player player)
        => player is OfflinePlayer bot ? Of(bot.Persona) : Profiles[(int)BotPersona.Balanced];

    /// <summary>
    /// Gets the profile of a persona.
    /// </summary>
    /// <param name="persona">The persona.</param>
    /// <returns>The profile.</returns>
    internal static BotPersonaProfile Of(BotPersona persona)
        => Profiles[(int)persona];

    /// <summary>
    /// Picks the persona of a character from the configured mix, deterministically from its identity: a
    /// bot keeps its persona across restarts and reports, so a behavior report always names the same
    /// character with the same style.
    /// </summary>
    /// <param name="mix">The mix, e.g. <c>balanced:70,aggressive:10</c>; empty or invalid entries fall back to <see cref="DefaultMix"/>.</param>
    /// <param name="loginName">The account login name.</param>
    /// <param name="characterName">The character name.</param>
    /// <returns>The persona.</returns>
    internal static BotPersona PickPersona(string? mix, string loginName, string characterName)
    {
        var entries = ParseMix(mix);
        var total = entries.Sum(e => e.Weight);
        if (total <= 0)
        {
            return BotPersona.Balanced;
        }

        var roll = (int)(Hash(loginName, characterName, 0) % (ulong)total);
        foreach (var (persona, weight) in entries)
        {
            if (roll < weight)
            {
                return persona;
            }

            roll -= weight;
        }

        return BotPersona.Balanced;
    }

    /// <summary>
    /// Derives the random seed of a bot session from its identity and an optional server salt. The seed is
    /// logged when the bot starts, so a report about a patterned bot can be replayed exactly.
    /// </summary>
    /// <param name="loginName">The account login name.</param>
    /// <param name="characterName">The character name.</param>
    /// <param name="serverSalt">An optional server-wide salt; 0 for none.</param>
    /// <returns>A non-zero seed.</returns>
    internal static ulong DeriveSeed(string loginName, string characterName, int serverSalt)
        => Hash(loginName, characterName, serverSalt) | 1;

    /// <summary>
    /// Parses a persona mix into weighted entries. Unknown persona names are ignored; when nothing valid
    /// is left, the default mix is used, so a typo never leaves the bots without a persona.
    /// </summary>
    /// <param name="mix">The configured mix.</param>
    /// <returns>The weighted personas.</returns>
    internal static IReadOnlyList<(BotPersona Persona, int Weight)> ParseMix(string? mix)
    {
        var parsed = ParseEntries(mix);
        return parsed.Count > 0 ? parsed : ParseEntries(DefaultMix);
    }

    private static List<(BotPersona Persona, int Weight)> ParseEntries(string? mix)
    {
        var result = new List<(BotPersona Persona, int Weight)>();
        if (string.IsNullOrWhiteSpace(mix))
        {
            return result;
        }

        foreach (var part in mix.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = part.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !Enum.TryParse<BotPersona>(parts[0], ignoreCase: true, out var persona)
                || !int.TryParse(parts[1], out var weight)
                || weight <= 0)
            {
                continue;
            }

            result.Add((persona, weight));
        }

        return result;
    }

    private static ulong Hash(string loginName, string characterName, int serverSalt)
    {
        // FNV-1a over the identity; the salt shifts the whole sequence for servers which want different
        // sessions from the same characters.
        var hash = 14695981039346656037UL;
        foreach (var value in new[] { loginName, characterName, serverSalt.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            hash ^= 0xFF;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
