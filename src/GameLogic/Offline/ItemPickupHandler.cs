// <copyright file="ItemPickupHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;

/// <summary>
/// Handles item and zen pickup for the offline player.
/// </summary>
public sealed class ItemPickupHandler
{
    private const byte MinPickupRange = 1;

    /// <summary>
    /// How often the player walks towards the same drop before giving up on it.
    /// </summary>
    private const int MaximumWalksPerDrop = 3;

    /// <summary>
    /// How long a drop is ignored after it could not be picked up or reached.
    /// </summary>
    private static readonly TimeSpan IgnoreDuration = TimeSpan.FromSeconds(30);

    private static readonly PickupItemAction PickupAction = new();

    private static readonly HashSet<ItemIdentifier> Jewels =
    [
        ItemConstants.JewelOfChaos,
        ItemConstants.JewelOfBless,
        ItemConstants.JewelOfSoul,
        ItemConstants.JewelOfLife,
        ItemConstants.JewelOfCreation,
        ItemConstants.JewelOfGuardian,
        ItemConstants.Gemstone,
        ItemConstants.JewelOfHarmony,
        ItemConstants.LowerRefineStone,
        ItemConstants.HigherRefineStone,
    ];

    private readonly OfflinePlayer _player;
    private readonly IMuHelperSettings? _config;
    private readonly MovementHandler? _movementHandler;
    private readonly Dictionary<ushort, DateTime> _ignoredDrops = [];

    private ushort? _walkTargetId;
    private int _walksToTarget;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemPickupHandler"/> class.
    /// </summary>
    /// <param name="player">The offline player.</param>
    /// <param name="config">The MU Helper configuration.</param>
    /// <param name="movementHandler">
    /// The movement handler to walk to drops which are out of pickup reach. Without it, such drops are left alone.
    /// </param>
    public ItemPickupHandler(OfflinePlayer player, IMuHelperSettings? config, MovementHandler? movementHandler = null)
    {
        this._player = player;
        this._config = config;
        this._movementHandler = movementHandler;
    }

    /// <summary>
    /// Scans for and picks up items within a configurable range.
    /// </summary>
    /// <remarks>
    /// The obtain range may be larger than the distance at which the server accepts a pick up
    /// (<see cref="PickupItemAction.MaximumPickupDistance"/>). Requesting such a drop from where the player
    /// stands was refused on every tick, forever; instead the player walks to the nearest wanted drop
    /// first, like the MU Helper of the client does. A drop which stays on the ground after a request
    /// (e.g. no inventory space, not an owner yet) or which can't be reached is ignored for a while.
    /// </remarks>
    public async ValueTask PickupItemsAsync()
    {
        if (this._config is null || this._player.CurrentMap is not { } map)
        {
            return;
        }

        if (!this._config.PickAllItems && !this._config.PickSelectItems)
        {
            return;
        }

        byte range = (byte)Math.Max(this._config.ObtainRange, MinPickupRange);
        var drops = map.GetDropsInRange(this._player.Position, range);
        var now = DateTime.UtcNow;
        this.ForgetExpiredIgnores(now);

        ILocateable? nearestOutOfReach = null;
        var nearestDistance = double.MaxValue;
        foreach (var drop in drops)
        {
            if (this._ignoredDrops.ContainsKey(drop.Id) || !this.ShouldPickUpDrop(drop))
            {
                continue;
            }

            var distance = this._player.GetDistanceTo(drop);
            if (distance <= PickupItemAction.MaximumPickupDistance)
            {
                await PickupAction.PickupItemAsync(this._player, drop.Id).ConfigureAwait(false);
                if (ReferenceEquals(map.GetDrop(drop.Id), drop))
                {
                    this._ignoredDrops[drop.Id] = now + IgnoreDuration;
                }
            }
            else if (distance < nearestDistance)
            {
                nearestOutOfReach = drop;
                nearestDistance = distance;
            }
        }

        if (nearestOutOfReach is not null)
        {
            await this.WalkToDropAsync(nearestOutOfReach, now).ConfigureAwait(false);
        }
    }

    private static bool IsJewel(Item item)
    {
        if (item.Definition is not { } definition)
        {
            return false;
        }

        return Jewels.Contains(new(definition.Number, definition.Group));
    }

    private bool ShouldPickUpDrop(IIdentifiable drop)
    {
        if (drop is DroppedMoney money && !this.CanTakeMoney(money))
        {
            return false;
        }

        if (this._config!.PickAllItems)
        {
            return true;
        }

        if (!this._config.PickSelectItems)
        {
            return false;
        }

        if (drop is DroppedMoney && this._config.PickZen)
        {
            return true;
        }

        if (drop is DroppedItem droppedItem)
        {
            return this.ShouldPickUp(droppedItem.Item);
        }

        return false;
    }

    /// <summary>
    /// Determines whether picking up the money would give the player anything.
    /// </summary>
    /// <remarks>
    /// A player at the money limit is refused every money drop. Walking to it anyway kept a player near
    /// the limit going back and forth between the money and its fight, every time the drop was no longer
    /// ignored. In a party, another member may take the money, so it's still picked up.
    /// </remarks>
    private bool CanTakeMoney(DroppedMoney money)
    {
        return this._player.Party is not null || MoneyDistribution.CanPay(this._player, money.Amount);
    }

    private bool ShouldPickUp(Item item)
    {
        if (this._config is null)
        {
            return false;
        }

        if (this._player.Account?.IsBot == true)
        {
            return this.ShouldPickUpBot(item, this._config);
        }

        if (this._config.PickJewel && IsJewel(item))
        {
            // A human's helper takes every jewel - its owner trades, crafts or hoards them later.
            return true;
        }

        var isAncient = item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0);
        var isExcellent = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent);
        if ((this._config.PickAncient && isAncient) || (this._config.PickExcellent && isExcellent))
        {
            // A human's helper hoards every excellent/ancient piece - its owner sorts the treasure out later.
            return true;
        }

        if (this._config.PickUpgradeItems && Bots.BotEquipmentHandler.IsUpgradeFor(this._player, item))
        {
            return true;
        }

        if (this._config.PickExtraItems && item.Definition is { } definition)
        {
            return this._config.ExtraItemNames.Any(name => definition.Name.ToString()?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return false;
    }

    /// <summary>
    /// The bot-specific pickup rule: the generic loot policy says whether the item is an upgrade, a
    /// jewel within the stock or a skill the bot still needs, and the helper configuration still gates
    /// the categories the bot collects. The sampled debug log records the reason.
    /// </summary>
    /// <param name="item">The dropped item.</param>
    /// <param name="config">The helper configuration.</param>
    /// <returns><c>true</c>, if the bot should pick the item up.</returns>
    private bool ShouldPickUpBot(Item item, IMuHelperSettings config)
    {
        if (Bots.BotLootPolicy.WantsQuestItem(this._player, item))
        {
            Bots.BotLootDecisionLog.Log(this._player, item, new(Bots.BotLootAction.Keep, Bots.BotLootReason.QuestItem), "pickup");
            return true;
        }

        if (config.PickJewel && IsJewel(item))
        {
            // A bot has no later: it spends Bless, Soul and Life on its own gear and Chaos at the chaos
            // machine, so a Jewel of Creation or a stock-exceeding Soul is a backpack slot it never gets back.
            var decision = Bots.BotLootPolicy.DecideForPickup(this._player, item);
            Bots.BotLootDecisionLog.Log(this._player, item, decision, "pickup");
            return decision.Action == Bots.BotLootAction.Keep;
        }

        var isAncient = item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0);
        var isExcellent = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent);
        if ((config.PickAncient && isAncient) || (config.PickExcellent && isExcellent))
        {
            // A bot cannot trade its treasures away, so it only takes what it can actually wear as an
            // upgrade; everything else would silt up its backpack until the loot pickup stops.
            var decision = Bots.BotLootPolicy.DecideForPickup(this._player, item);
            Bots.BotLootDecisionLog.Log(this._player, item, decision, "pickup");
            return decision.Action == Bots.BotLootAction.Equip;
        }

        if (config.PickUpgradeItems)
        {
            // Gear which beats what the bot wears, or an orb/scroll teaching a skill it does not know yet.
            // The equipment and skill handlers consume it on their next passes.
            var decision = Bots.BotLootPolicy.DecideForPickup(this._player, item);
            if (decision.Action == Bots.BotLootAction.Equip || decision.Reason == Bots.BotLootReason.SkillItem)
            {
                Bots.BotLootDecisionLog.Log(this._player, item, decision, "pickup");
                return true;
            }
        }

        if (config.PickExtraItems && item.Definition is { } definition
            && config.ExtraItemNames.Any(name => definition.Name.ToString()?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            Bots.BotLootDecisionLog.Log(
                this._player,
                item,
                new(Bots.BotLootAction.Keep, Bots.BotLootReason.ConfiguredExtra),
                "pickup");
            return true;
        }

        return false;
    }

    private async ValueTask WalkToDropAsync(ILocateable drop, DateTime now)
    {
        if (this._movementHandler is null || this._player.IsWalking)
        {
            return;
        }

        if (this._walkTargetId != drop.Id)
        {
            this._walkTargetId = drop.Id;
            this._walksToTarget = 0;
        }

        this._walksToTarget++;
        if (this._walksToTarget > MaximumWalksPerDrop
            || !await this._movementHandler.WalkToPositionAsync(drop.Position).ConfigureAwait(false))
        {
            this._ignoredDrops[drop.Id] = now + IgnoreDuration;
            this._walkTargetId = null;
        }
    }

    private void ForgetExpiredIgnores(DateTime now)
    {
        if (this._ignoredDrops.Count == 0)
        {
            return;
        }

        foreach (var (id, until) in this._ignoredDrops.ToList())
        {
            if (until <= now)
            {
                this._ignoredDrops.Remove(id);
            }
        }
    }
}
