// <copyright file="HealingHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions.ItemConsumeActions;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Handles health recovery for the offline player and their party.
/// </summary>
public sealed class HealingHandler
{
    /// <summary>
    /// The health potions the offline player drinks, best first. Also the shopping list a bot restocks
    /// from (see <c>BotShoppingHandler</c>): buying what it does not drink, or not buying what it does,
    /// is how a bot ends up starving next to a full merchant.
    /// </summary>
    internal static readonly ItemIdentifier[] HealthPotionPriority =
    [
        ItemConstants.LargeHealingPotion,
        ItemConstants.MediumHealingPotion,
        ItemConstants.SmallHealingPotion,
        ItemConstants.Apple,
    ];

    /// <summary>
    /// The mana potions the offline player drinks, best first. <see cref="HealthPotionPriority"/>.
    /// </summary>
    internal static readonly ItemIdentifier[] ManaPotionPriority =
    [
        ItemConstants.LargeManaPotion,
        ItemConstants.MediumManaPotion,
        ItemConstants.SmallManaPotion,
    ];

    /// <summary>The shield potions a bot can use in a player fight, best first.</summary>
    internal static readonly ItemIdentifier[] ShieldPotionPriority =
    [
        ItemConstants.LargeShieldPotion,
        ItemConstants.MediumShieldPotion,
        ItemConstants.SmallShieldPotion,
    ];

    /// <summary>Drink a mana potion once mana falls below this share, so casters can keep casting.</summary>
    private const int ManaThresholdPercent = 30;

    private static readonly ItemConsumeAction ConsumeAction = new();

    private readonly OfflinePlayer _player;
    private readonly IMuHelperSettings? _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealingHandler"/> class.
    /// </summary>
    /// <param name="player">The offline player.</param>
    /// <param name="config">The MU helper settings.</param>
    public HealingHandler(OfflinePlayer player, IMuHelperSettings? config)
    {
        this._player = player;
        this._config = config;
    }

    /// <summary>
    /// Performs health recovery actions for the player and their party.
    /// </summary>
    public async ValueTask PerformHealthRecoveryAsync()
    {
        if (this._config is null || this._player.Attributes is null)
        {
            return;
        }

        await this.PerformSelfHealingAsync().ConfigureAwait(false);

        await this.PerformPartyHealingAsync().ConfigureAwait(false);

        await this.PerformManaRecoveryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the recovery of a bot in a normal tick: health, then shield, then mana.
    /// </summary>
    /// <param name="hasPlayerOpponent">Whether a legal player opponent is currently engaged.</param>
    /// <remarks>
    /// All recover potions share <see cref="Player.PotionCooldownUntil"/>, so the order decides which
    /// one is actually drunk. Health keeps the bot alive and comes first, the shield soaks 90% of the
    /// damage of a player fight, and mana only comes last - otherwise a bot with a low mana bar drinks
    /// a mana potion on every tick and never refills its shield.
    /// </remarks>
    internal async ValueTask PerformBotRecoveryAsync(bool hasPlayerOpponent)
    {
        if (this._config is null || this._player.Attributes is null)
        {
            return;
        }

        await this.PerformSelfHealingAsync().ConfigureAwait(false);
        await this.PerformPartyHealingAsync().ConfigureAwait(false);
        await this.PerformBotShieldRecoveryAsync(hasPlayerOpponent).ConfigureAwait(false);
        await this.PerformManaRecoveryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Uses potions during a bot's combo without casting a healing skill that interrupts it.
    /// </summary>
    /// <param name="hasPlayerOpponent">Whether a legal player opponent is currently engaged.</param>
    internal async ValueTask PerformBotComboRecoveryAsync(bool hasPlayerOpponent)
    {
        if (this._config is null || this._player.Attributes is null)
        {
            return;
        }

        // Health comes first, but it must not block the other potions: a chain which stopped because the
        // mana bar ran dry has to be able to continue while the bot is still hurt - and a bot whose
        // healing potions are used up drinks a mana potion instead of doing nothing at all.
        if (this._config.UseHealPotion
            && this.IsHealthBelowThreshold(this._player, this._config.PotionThresholdPercent)
            && await this.UseHealthPotionAsync().ConfigureAwait(false))
        {
            return;
        }

        await this.PerformBotShieldRecoveryAsync(hasPlayerOpponent).ConfigureAwait(false);
        await this.PerformManaRecoveryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Uses an existing shield potion if the bot is in a player fight and its shield is low.
    /// </summary>
    /// <param name="hasPlayerOpponent">Whether a legal player opponent is currently engaged.</param>
    internal async ValueTask PerformBotShieldRecoveryAsync(bool hasPlayerOpponent)
    {
        if (!hasPlayerOpponent
            || this._player.Account?.IsBot != true
            || this._player.Attributes is not { } attributes
            || attributes[Stats.MaximumShield] <= 0
            || attributes[Stats.CurrentShield] * 100 > attributes[Stats.MaximumShield] * BotMuHelperSettings.ShieldPotionThresholdPercent)
        {
            return;
        }

        await this.UsePotionAsync(ShieldPotionPriority).ConfigureAwait(false);
    }

    /// <summary>
    /// Drinks a mana potion when the mana bar ran low. Runs after the health and shield potions, which
    /// share the same potion cooldown.
    /// </summary>
    private async ValueTask PerformManaRecoveryAsync()
    {
        if (this._config?.UseManaPotion == true && this.IsManaBelowThreshold())
        {
            await this.UsePotionAsync(ManaPotionPriority).ConfigureAwait(false);
        }
    }

    private async ValueTask PerformSelfHealingAsync()
    {
        if (this.IsHealthBelowThreshold(this._player, this._config!.HealThresholdPercent))
        {
            var healSkill = this.FindSkillByType(SkillType.Regeneration);
            if (healSkill is not null && this._config.AutoHeal)
            {
                await this._player.ForEachWorldObserverAsync<IShowSkillAnimationPlugIn>(
                    p => p.ShowSkillAnimationAsync(this._player, this._player, healSkill.Skill!, true),
                    includeThis: true).ConfigureAwait(false);
                await this._player.ApplyRegenerationAsync(this._player, healSkill).ConfigureAwait(false);
                return;
            }
        }

        if (this._config!.UseHealPotion
            && this.IsHealthBelowThreshold(this._player, this._config.PotionThresholdPercent))
        {
            await this.UseHealthPotionAsync().ConfigureAwait(false);
        }
    }

    private bool IsManaBelowThreshold()
    {
        if (this._player.Attributes is not { } attributes)
        {
            return false;
        }

        double mana = attributes[Stats.CurrentMana];
        double maxMana = attributes[Stats.MaximumMana];
        return maxMana > 0 && (mana * 100.0 / maxMana) <= ManaThresholdPercent;
    }

    private async ValueTask PerformPartyHealingAsync()
    {
        if (!this._config!.AutoHealParty || this._player.Party is not { } party)
        {
            return;
        }

        var healSkill = this.FindSkillByType(SkillType.Regeneration);
        if (healSkill is null)
        {
            return;
        }

        foreach (var member in party.PartyList.OfType<Player>())
        {
            if (member == this._player)
            {
                continue;
            }

            if (!member.IsActive()
                || member.CurrentMap != this._player.CurrentMap
                || !this._player.IsInRange(member, this._config!.HuntingRange))
            {
                continue;
            }

            if (this.IsHealthBelowThreshold(member, this._config.HealPartyThresholdPercent))
            {
                await this._player.ForEachWorldObserverAsync<IShowSkillAnimationPlugIn>(
                    p => p.ShowSkillAnimationAsync(this._player, member, healSkill.Skill!, true),
                    includeThis: true).ConfigureAwait(false);

                await member.ApplyRegenerationAsync(this._player, healSkill).ConfigureAwait(false);
            }
        }
    }

    private bool IsHealthBelowThreshold(IAttackable target, int thresholdPercent)
    {
        if (target.Attributes is not { } attributes)
        {
            return false;
        }

        double hp = attributes[Stats.CurrentHealth];
        double maxHp = attributes[Stats.MaximumHealth];
        return maxHp > 0 && (hp * 100.0 / maxHp) <= thresholdPercent;
    }

    private ValueTask<bool> UseHealthPotionAsync() => this.UsePotionAsync(HealthPotionPriority);

    /// <summary>
    /// Drinks the best available potion of the given priority list.
    /// </summary>
    /// <param name="priority">The potions, best first.</param>
    /// <returns><c>true</c> if a potion was found and used; <c>false</c> if none of them is in the inventory.</returns>
    private async ValueTask<bool> UsePotionAsync(ItemIdentifier[] priority)
    {
        if (this._player.Inventory is null)
        {
            return false;
        }

        foreach (var identifier in priority)
        {
            var potion = this._player.Inventory.Items
                .FirstOrDefault(i => i.Definition?.Group == identifier.Group
                                     && i.Definition.Number == identifier.Number);
            if (potion is not null)
            {
                await ConsumeAction.HandleConsumeRequestAsync(
                    this._player, potion.ItemSlot, potion.ItemSlot, FruitUsage.Undefined).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private SkillEntry? FindSkillByType(SkillType type)
    {
        return this._player.SkillList?.Skills.FirstOrDefault(s => s.Skill?.SkillType == type);
    }
}