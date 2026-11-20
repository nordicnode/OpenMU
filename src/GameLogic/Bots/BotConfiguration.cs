// <copyright file="BotConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// The admin-panel editable configuration of the <see cref="BotFeaturePlugIn"/>.
/// </summary>
public class BotConfiguration
{
    /// <summary>
    /// The hard limit of characters a single account can hold in the game.
    /// </summary>
    public const int MaxCharactersPerAccountLimit = 5;

    /// <summary>
    /// Gets or sets a value indicating whether the bot feature is enabled.
    /// Disabled by default so that enabling bots is always an explicit, deliberate action.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_Enabled_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_Enabled_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all bot accounts and characters should be deleted.
    /// When set, the feature purges every bot account on the next startup before generating fresh
    /// ones, and then automatically clears this flag again. Use it to reset the bot population.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_ResetBots_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_ResetBots_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool ResetBots { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether all bot accounts and characters should be deleted
    /// WITHOUT being regenerated. Unlike <see cref="ResetBots"/> this also turns <see cref="Enabled"/>
    /// off - otherwise the very same pass would generate the population again - so it is the single
    /// switch for "I do not want bots on this server anymore". Clears itself afterwards.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PurgeBots_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PurgeBots_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool PurgeBots { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly generated bot characters start as fresh
    /// characters, the way a player who just created one does: at level 1 with level-0 starter
    /// equipment, no reset history and only the skills its level-1 stats allow. This only affects
    /// characters which are generated from now on - an existing bot population keeps its state
    /// until it is regenerated (<see cref="ResetBots"/>).
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_StartAsFreshCharacters_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_StartAsFreshCharacters_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool StartAsFreshCharacters { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bot population rotates its presence over the day:
    /// fewer bots are online at night, most in the evening, with bots smoothly logging in and out -
    /// like a real player base, instead of the same characters being online 24/7.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PresenceRotation_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PresenceRotation_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool PresenceRotation { get; set; } = true;

    /// <summary>
    /// Gets or sets the share (in percent) of bots which stays online at the quietest time of day.
    /// 100 effectively disables the rotation effect.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MinOnlineSharePercent_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MinOnlineSharePercent_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public int MinOnlineSharePercent { get; set; } = 60;

    /// <summary>
    /// Gets or sets the number of bot accounts. Together with <see cref="MaxCharactersPerAccount"/>
    /// this defines the generated bot population, e.g. 10 accounts × 5 characters = 50 bot characters.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_NumberOfAccounts_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_NumberOfAccounts_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(0, 1000)]
    public int NumberOfAccounts { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of characters per bot account. An account can hold at most
    /// <see cref="MaxCharactersPerAccountLimit"/> (5) characters, so this value is clamped on use.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MaxCharactersPerAccount_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MaxCharactersPerAccount_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(1, MaxCharactersPerAccountLimit)]
    public int MaxCharactersPerAccount { get; set; } = MaxCharactersPerAccountLimit;

    /// <summary>
    /// Gets or sets the share (in percent) of a game server's maximum player count which its bots may
    /// occupy. Bots count towards that limit like players do, and a full server turns new clients away -
    /// so the rest of the capacity stays reserved for real players, who must never be denied a slot by a
    /// bot. The population is split over all configured game servers accordingly (see
    /// <see cref="BotServerPartition"/>); accounts which do not fit stay offline until the servers offer
    /// the room for them.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_BotCapacityPercent_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_BotCapacityPercent_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(1, 100)]
    public int BotCapacityPercent { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether bots pay the configured reset costs (zen, reset items)
    /// when they reset their character on a server with the reset feature enabled. Off by default:
    /// bots don't take part in the player economy the costs are balanced for, so charging them only
    /// stalls their progression (a bot can't farm zen for a billion-zen reset the way players trade).
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_BotsPayResetCosts_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_BotsPayResetCosts_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool BotsPayResetCosts { get; set; }

    /// <summary>
    /// Gets or sets how many Jewels of Bless, Soul, Life and Chaos a bot keeps of each kind. Bots only pick up
    /// the jewels they can actually spend on their own gear (see <c>BotJewelHandler</c>) and stop
    /// collecting a kind once they hold this many; whatever they carry above it is sold on the next
    /// merchant visit. The sensible value depends entirely on the server's drop rates - on a high rate
    /// server a bot refills a big stock within hours, so a low limit keeps its backpack usable.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_JewelStockPerKind_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_JewelStockPerKind_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(0, 100)]
    public int JewelStockPerKind { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of potion charges (per healing and per mana potions) a bot stocks up to
    /// at a merchant. Merchants sell potions in stacks of different sizes, so this is the target the bot
    /// buys towards, not a stack count.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PotionStockCharges_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PotionStockCharges_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(10, 255)]
    public int PotionStockCharges { get; set; } = 60;

    /// <summary>
    /// Gets or sets the share (in percent) of loot decisions which a bot writes to the debug log, so an
    /// operator can follow why it kept, equipped, sold or stored an item. The decisions are sampled per
    /// item: 0 switches the audit off, 100 logs every decision. The default keeps a busy server's log
    /// readable while still showing the shape of the policy.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_LootDecisionLogPercent_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_LootDecisionLogPercent_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(0, 100)]
    public int LootDecisionLogPercent { get; set; } = 5;

    /// <summary>
    /// Gets or sets how many master points a bot may hold back for a master skill which costs several
    /// points at once. A bot otherwise invests every point the moment it earns it, so a skill whose
    /// minimum level is ten points - and everything behind it in the same branch - could never be
    /// learned. With a cap of ten it saves up for such a skill; 0 invests every point immediately, as
    /// before. The cap is the safety fallback: a skill costing more than the cap is never saved for, so
    /// the bot does not hoard points forever on a tree it cannot reach.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MasterPointBankingCap_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MasterPointBankingCap_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(0, 50)]
    public int MasterPointBankingCap { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether a bot casts the enemy debuffs of its class (the summoner's
    /// Sleep, Weakness and Innovation) on its target, instead of only buffing itself. A debuff is never
    /// part of the self-buff rotation, so it can't be turned on the bot itself.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_UseEnemyDebuffs_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_UseEnemyDebuffs_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool UseEnemyDebuffs { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether bots take part in the duel system: they accept requests
    /// of players of a comparable level and now and then challenge one themselves. Off by default -
    /// duels change the PvP experience of a server, so they are an explicit, deliberate choice. A bot
    /// never duels while it is busy with an errand, an event, a market shift or a grudge.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_Duels_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_Duels_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool Duels { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether bots buy equipment upgrades from the merchants they
    /// visit. Off by default and deliberately opt-in: bots earn plenty of Zen, and letting them spend
    /// it on gear changes the economy of a server which balances prices around trading players.
    /// When enabled, each purchase is capped by <see cref="EquipmentBudgetPercent"/> so a bot never
    /// empties its purse for one piece.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_BuyEquipment_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_BuyEquipment_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public bool BuyEquipment { get; set; }

    /// <summary>
    /// Gets or sets the share (in percent) of a bot's money which may go into buying equipment on a
    /// single shopping trip. Only used when <see cref="BuyEquipment"/> is enabled. A piece costlier
    /// than this share of the bot's purse is left on the shelf - the bot keeps its potion and warp
    /// money.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_EquipmentBudgetPercent_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_EquipmentBudgetPercent_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(1, 100)]
    public int EquipmentBudgetPercent { get; set; } = 50;

    /// <summary>
    /// Gets or sets the weighted mix of play style personas the population is drawn from, e.g.
    /// <c>balanced:70,aggressive:10,cautious:10,trader:5,social:5</c>. A persona only shifts a few bounded
    /// dials (combat headroom, shopping trigger, reaction delay) - it never changes what a bot may do.
    /// Unknown names and empty entries are ignored; when nothing valid is left, the default mix is used.
    /// The persona of a character is derived from its account and name, so it stays the same across
    /// restarts and can be named in a report.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PersonaMix_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_PersonaMix_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public string PersonaMix { get; set; } = BotPersonaProfile.DefaultMix;

    /// <summary>
    /// Gets or sets an optional server-wide salt for the per-bot random seeds. The seed of a bot session
    /// is derived from its account, its character name and this salt, and logged when the bot starts, so a
    /// report can be replayed exactly. Change it to give an identical population a different behavior
    /// noise; keep it to keep the sessions reproducible.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_RandomSeed_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_RandomSeed_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    [Range(0, 1000000)]
    public int RandomSeed { get; set; }

    /// <summary>
    /// Gets or sets the maps bots never warp to: they don't pick them as hunting grounds, don't follow
    /// a party leader there, don't enter events held on them, and a bot which logs in on one leaves it.
    /// Players are not affected. Empty by default; servers use it for maps where a bot does more harm
    /// than good, like the Arena (players' duels and cage tricks) or Crywolf.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MapsForbiddenForBots_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_MapsForbiddenForBots_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public ICollection<GameMapDefinition> MapsForbiddenForBots { get; set; } = new List<GameMapDefinition>();

    /// <summary>
    /// Gets or sets a comma separated list of login names of existing accounts to animate as bots.
    /// This is an optional extra hook alongside the generated population (see
    /// <see cref="NumberOfAccounts"/>): every listed account gets a bot driving its first character.
    /// These accounts are animated as-is and are not part of the partitioned, capacity-limited
    /// population, so leave it empty unless you specifically want to drive existing accounts.
    /// </summary>
    [Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_ProofOfConceptAccounts_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotConfiguration_ProofOfConceptAccounts_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
    public string ProofOfConceptAccounts { get; set; } = string.Empty;

    /// <summary>
    /// Whether bots may not warp to the map. Compared by map number: the definitions of the running maps
    /// and the ones referenced by this configuration aren't always the same instances.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <returns><c>True</c>, if the map is in <see cref="MapsForbiddenForBots"/>.</returns>
    /// <remarks>Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>.</remarks>
    public bool IsMapForbidden(GameMapDefinition? map)
        => map is not null && this.MapsForbiddenForBots.Any(m => m.Number == map.Number);

    /// <summary>
    /// Gets the effective, clamped number of characters per account.
    /// </summary>
    /// <returns>A value between 1 and <see cref="MaxCharactersPerAccountLimit"/>.</returns>
    /// <remarks>Deliberately a method: a get-only property would end up in the serialized plugin configuration JSON.</remarks>
    public int GetEffectiveCharactersPerAccount()
        => Math.Clamp(this.MaxCharactersPerAccount, 1, MaxCharactersPerAccountLimit);

    /// <summary>
    /// Gets the effective, clamped share of a server's player capacity which its bots may occupy.
    /// </summary>
    /// <returns>A value between 1 and 100.</returns>
    /// <remarks>Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>.</remarks>
    public int GetEffectiveBotCapacityPercent()
        => Math.Clamp(this.BotCapacityPercent, 1, 100);

    /// <summary>
    /// Gets the effective, clamped jewel stock a bot keeps of each usable kind.
    /// </summary>
    /// <returns>A value between 0 and 100.</returns>
    /// <remarks>Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>.</remarks>
    public int GetEffectiveJewelStockPerKind()
        => Math.Clamp(this.JewelStockPerKind, 0, 100);

    /// <summary>
    /// Gets the effective, clamped potion charges a bot stocks up to.
    /// </summary>
    /// <returns>A value between 10 and 255.</returns>
    /// <remarks>Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>.</remarks>
    public int GetEffectivePotionStockCharges()
        => Math.Clamp(this.PotionStockCharges, 10, 255);

    /// <summary>
    /// Gets the effective, clamped share of loot decisions which a bot writes to the debug log.
    /// </summary>
    /// <returns>A value between 0 and 100.</returns>
    /// <remarks>
    /// Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>. A configuration which
    /// predates this setting reads 0 and keeps the audit off after an upgrade until an operator turns it
    /// on; only a newly created configuration starts at the default of 5.
    /// </remarks>
    public int GetEffectiveLootDecisionLogSamplePercent()
        => Math.Clamp(this.LootDecisionLogPercent, 0, 100);

    /// <summary>
    /// Gets the effective, clamped number of master points a bot may bank.
    /// </summary>
    /// <returns>A value between 0 and 50.</returns>
    /// <remarks>Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>.</remarks>
    public int GetEffectiveMasterPointBankingCap()
        => Math.Clamp(this.MasterPointBankingCap, 0, 50);

    /// <summary>
    /// Gets the effective, clamped share of a bot's money which may go into equipment.
    /// </summary>
    /// <returns>A value between 1 and 100.</returns>
    /// <remarks>Deliberately a method, like <see cref="GetEffectiveCharactersPerAccount"/>.</remarks>
    public int GetEffectiveEquipmentBudgetPercent()
        => Math.Clamp(this.EquipmentBudgetPercent, 1, 100);

    /// <summary>
    /// Parses <see cref="ProofOfConceptAccounts"/> into the distinct, trimmed login names.
    /// </summary>
    /// <returns>The list of login names.</returns>
    /// <remarks>Deliberately a method: a get-only property would end up in the serialized plugin configuration JSON.</remarks>
    public IReadOnlyList<string> ParseProofOfConceptAccounts()
        => this.ProofOfConceptAccounts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
