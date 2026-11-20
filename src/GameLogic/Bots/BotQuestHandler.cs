// <copyright file="BotQuestHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Lets a bot earn the quest-gated combo the way a player does: it finds the quest giver (Marlon, who
/// wanders between the starter towns), starts the quest, farms the monsters which drop its item, and
/// hands it in. Which quest that is comes entirely from the configuration - the handler only looks for
/// quests whose reward unlocks <see cref="Stats.IsSkillComboAvailable"/>, so a server without that
/// chain simply has no quest goal.
/// </summary>
/// <remarks>
/// The navigation stays in <see cref="BotNavigator"/>; this class only answers what the goal is, where
/// the giver currently stands and which monsters count for the quest. The dialog actions run in the MU
/// Helper tick like every other bot mutation (see <see cref="OfflinePlayer.PendingBotActions"/>).
/// </remarks>
internal static class BotQuestHandler
{
    /// <summary>
    /// How often a bot re-checks its quest goal. The giver wanders at most once per hour, so the check
    /// is cheap and does not need to run on every navigation tick.
    /// </summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a bot waits after a quest trip which found no giver or no route, so a missing NPC or an
    /// unreachable map does not send it marching every check interval.
    /// </summary>
    internal static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(5);

    private static readonly TalkNpcAction TalkAction = new();
    private static readonly QuestStartAction StartAction = new();
    private static readonly QuestCompletionAction CompletionAction = new();
    private static readonly CloseNpcDialogAction CloseAction = new();

    /// <summary>
    /// How often the live position of a wandering quest giver is resolved again. Marlon moves every one
    /// to three hours, so a shared answer is reused by every bot in the meantime instead of scanning the
    /// possible maps once per bot.
    /// </summary>
    private static readonly TimeSpan GiverLocationLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The quest chain per configuration, keyed weakly so a thrown-away test configuration does not stay
    /// alive. Only quests whose reward unlocks the combo are cached; the chain is assembled from the
    /// giver's quests at use time, because the qualified class depends on the character.
    /// </summary>
    private static readonly ConditionalWeakTable<GameConfiguration, (MonsterDefinition Giver, QuestDefinition Quest)[]> ComboQuestCache = new();

    /// <summary>
    /// The last resolved position of a wandering giver per configuration and giver number. A miss is
    /// cached as well - a giver which is not spawned at all stays unspawned for a while.
    /// </summary>
    private static readonly ConditionalWeakTable<GameConfiguration, ConcurrentDictionary<short, CachedGiverLocation>> GiverLocationCache = new();

    /// <summary>
    /// The steps of a bot's quest journey.
    /// </summary>
    internal enum QuestProgress
    {
        /// <summary>The bot has nothing to do for the quest.</summary>
        None,

        /// <summary>The quest has not been started yet and the bot has to visit the giver.</summary>
        NotStarted,

        /// <summary>The quest runs, but its requirements are not met yet.</summary>
        InProgress,

        /// <summary>Every requirement is met and the bot has to return to the giver.</summary>
        ReadyToComplete,

        /// <summary>The quest is finished; there is nothing left to do for it.</summary>
        Finished,
    }

    /// <summary>
    /// Gets the next quest a bot has to run to unlock its combo, or <c>null</c> if the server has no
    /// such chain, the class is not offered it, the bot is too low, or the combo is already unlocked.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <returns>The quest goal, or <c>null</c>.</returns>
    internal static QuestGoal? GetComboQuestGoal(Player player)
    {
        if (player.Attributes?[Stats.IsSkillComboAvailable] is > 0
            || player.SelectedCharacter is not { } character)
        {
            return null;
        }

        foreach (var (giver, comboQuest) in GetComboQuests(player.GameContext.Configuration))
        {
            if (comboQuest.QualifiedCharacter is { } qualified && !Equals(qualified, character.CharacterClass))
            {
                continue;
            }

            if (comboQuest.EvolvesIntoInactiveClass(player))
            {
                continue;
            }

            // The chain is every eligible quest of the same group up to the one which unlocks the combo.
            // Taking the lowest unfinished one makes the bot run Hero Status before the Dark Stone, just
            // like a player who follows the dialog's order.
            var chain = giver.Quests
                .Where(quest => quest.Group == comboQuest.Group && quest.Number <= comboQuest.Number)
                .Where(quest => quest.QualifiedCharacter is null || Equals(quest.QualifiedCharacter, character.CharacterClass))
                .Where(quest => !quest.EvolvesIntoInactiveClass(player))
                .Where(quest => quest.MinimumCharacterLevel <= player.Level
                                && (quest.MaximumCharacterLevel == 0 || player.Level <= quest.MaximumCharacterLevel))
                .OrderBy(quest => quest.Number)
                .ToList();
            if (chain.Count == 0)
            {
                // The combo quest's level is not reached yet; no other quest of the chain helps.
                continue;
            }

            var state = player.GetQuestState(comboQuest.Group);
            if (state?.ActiveQuest is { } active)
            {
                var activeQuest = chain.FirstOrDefault(quest => quest.Number == active.Number);
                return activeQuest is null ? null : new QuestGoal(giver, activeQuest);
            }

            var next = chain.FirstOrDefault(quest => (state?.LastFinishedQuest?.Number ?? -1) < quest.Number);
            return next is null ? null : new QuestGoal(giver, next);
        }

        return null;
    }

    /// <summary>
    /// Gets how far a quest has progressed for the bot.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="quest">The quest.</param>
    /// <returns>The progress.</returns>
    internal static QuestProgress GetProgress(Player player, QuestDefinition quest)
    {
        var state = player.GetQuestState(quest.Group);
        if (state?.ActiveQuest is null)
        {
            return state?.LastFinishedQuest is { } last && last.Number >= quest.Number
                ? QuestProgress.Finished
                : QuestProgress.NotStarted;
        }

        if (state.ActiveQuest.Number != quest.Number)
        {
            // Another quest of the group is active; the bot finishes that one first.
            return QuestProgress.None;
        }

        return HasMetRequirements(player, state, state.ActiveQuest)
            ? QuestProgress.ReadyToComplete
            : QuestProgress.InProgress;
    }

    /// <summary>
    /// Gets what a bot has to hunt for an active quest, or <c>null</c> when nothing is left to hunt.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="quest">The active quest.</param>
    /// <returns>The farm target, or <c>null</c>.</returns>
    internal static FarmTarget? GetFarmTarget(Player player, QuestDefinition quest)
    {
        var state = player.GetQuestState(quest.Group);
        if (state?.ActiveQuest is null || state.ActiveQuest.Number != quest.Number)
        {
            return null;
        }

        var monsters = new List<MonsterDefinition>();
        foreach (var requirement in quest.RequiredMonsterKills)
        {
            var killCount = state.RequirementStates
                .FirstOrDefault(requirementState => Equals(requirementState.Requirement, requirement))
                ?.KillCount ?? 0;
            if (killCount < requirement.MinimumNumber && requirement.Monster is { } monster && !monsters.Contains(monster))
            {
                monsters.Add(monster);
            }
        }

        var bands = new List<(int MinimumLevel, int MaximumLevel)>();
        foreach (var requirement in quest.RequiredItems)
        {
            if (CountItem(player.Inventory?.Items, requirement) >= requirement.MinimumNumber)
            {
                continue;
            }

            if (requirement.DropItemGroup is not { } dropGroup)
            {
                // No drop group means the item cannot be farmed; the bot keeps hunting but gains nothing.
                continue;
            }

            if (dropGroup.Monster is { } dropMonster)
            {
                if (!monsters.Contains(dropMonster))
                {
                    monsters.Add(dropMonster);
                }

                continue;
            }

            var minimumLevel = dropGroup.MinimumMonsterLevel ?? 0;
            var maximumLevel = dropGroup.MaximumMonsterLevel ?? byte.MaxValue;
            if (minimumLevel <= maximumLevel)
            {
                bands.Add((minimumLevel, maximumLevel));
            }
        }

        return monsters.Count == 0 && bands.Count == 0
            ? null
            : new FarmTarget(monsters, bands);
    }

    /// <summary>
    /// Determines whether the bot still has to hunt for its combo quest, which keeps the navigator from
    /// warping away to a better farming map in the middle of the quest.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <returns><c>true</c> if an active combo quest still needs farming.</returns>
    internal static bool NeedsQuestFarm(Player player)
    {
        return GetComboQuestGoal(player) is { } goal
               && GetProgress(player, goal.Quest) == QuestProgress.InProgress
               && GetFarmTarget(player, goal.Quest) is not null;
    }

    /// <summary>
    /// Finds a map whose permanent spawns can advance the quest, preferring the map the bot already
    /// stands on and then the one with the most matching spawns. Remembering the map choice would be
    /// wrong here: a wandering giver or a changed level band is cheap to re-evaluate.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="target">The farm target.</param>
    /// <param name="isReachable">Tells whether the bot may travel to a map.</param>
    /// <returns>The map, or <c>null</c> if no reachable map holds the quest's monsters.</returns>
    internal static GameMapDefinition? PickQuestMap(
        Player player,
        FarmTarget target,
        Func<GameMapDefinition, bool> isReachable)
    {
        var currentNumber = player.CurrentMap?.Definition.Number;
        var candidates = player.GameContext.Configuration.Maps
            .Where(map => !ReferenceEquals(map, null))
            .Select(map => (Map: map, Spawns: map.MonsterSpawns
                .Where(BotHuntingGroundSelector.IsPermanentMonsterSpawn)
                .Where(spawn => spawn.MonsterDefinition is { } monster && target.Matches(monster))
                .ToList()))
            .Where(candidate => candidate.Spawns.Count > 0)
            .Where(candidate => candidate.Map.Number == currentNumber || isReachable(candidate.Map))
            .Where(candidate => BotWarpPlanner.HasWalkableSpawnGate(candidate.Map))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        // Already standing on a usable map: farming there is always cheaper than warping.
        if (candidates.FirstOrDefault(candidate => candidate.Map.Number == currentNumber) is { Map: { } current })
        {
            return current;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Spawns.Sum(spawn => spawn.Quantity))
            .Select(candidate => candidate.Map)
            .First();
    }

    /// <summary>
    /// Picks a walkable spot in a spawn area of the quest's monsters on the given map.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="target">The farm target.</param>
    /// <param name="from">The point to prefer, usually the bot's position.</param>
    /// <param name="ground">The picked ground.</param>
    /// <returns><c>true</c> if the map holds a usable ground.</returns>
    internal static bool TryPickQuestGround(GameMap map, FarmTarget target, Point from, out Point ground)
    {
        var areas = map.Definition.MonsterSpawns
            .Where(BotHuntingGroundSelector.IsPermanentMonsterSpawn)
            .Where(area => area.MonsterDefinition is { } monster && target.Matches(monster))
            .OrderBy(area => BotHuntingGroundSelector.GroundDistance(area, from))
            .ToList();
        foreach (var area in areas)
        {
            if (BotHuntingGroundSelector.TryPickWalkablePoint(map, area, null, [], null, 0, out ground))
            {
                return true;
            }
        }

        ground = default;
        return false;
    }

    /// <summary>
    /// Finds the live quest giver, on the bot's current map first and then on every configured map which
    /// has a spawn of the giver. The answer is shared between bots for a short while, because a wandering
    /// giver changes maps every one to three hours, not every tick.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="giver">The quest giver definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The map and position of the giver, or <c>null</c> if it is not spawned anywhere.</returns>
    internal static async ValueTask<(GameMap Map, Point Position)?> FindGiverAsync(
        OfflinePlayer player,
        MonsterDefinition giver,
        CancellationToken cancellationToken)
    {
        if (player.CurrentMap is { } currentMap && FindLiveNpcPosition(currentMap, giver) is { } currentPosition)
        {
            return (currentMap, currentPosition);
        }

        var configuration = player.GameContext.Configuration;
        var cache = GiverLocationCache.GetValue(configuration, static _ => new ConcurrentDictionary<short, CachedGiverLocation>());
        if (cache.TryGetValue(giver.Number, out var cached)
            && DateTimeOffset.UtcNow - cached.AtUtc < GiverLocationLifetime)
        {
            return await ResolveCachedAsync(player, giver, cached, cancellationToken).ConfigureAwait(false);
        }

        (GameMap Map, Point Position)? found = null;
        foreach (var mapDefinition in configuration.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mapDefinition.MonsterSpawns.Any(spawn => Equals(spawn.MonsterDefinition, giver)))
            {
                continue;
            }

            var map = await player.GameContext.GetMapAsync((ushort)mapDefinition.Number).ConfigureAwait(false);
            if (map is not null && FindLiveNpcPosition(map, giver) is { } position)
            {
                found = (map, position);
                break;
            }
        }

        cache[giver.Number] = new CachedGiverLocation(
            DateTimeOffset.UtcNow,
            found is { } value ? (ushort)value.Map.Definition.Number : null,
            found?.Position ?? default);
        return found;
    }

    /// <summary>
    /// Starts a quest at the giver near the given position. Runs inside the MU Helper tick.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="map">The map the giver stands on.</param>
    /// <param name="giverPosition">The position of the giver.</param>
    /// <param name="quest">The quest to start.</param>
    /// <returns><c>true</c> if the dialog was opened and the start was requested.</returns>
    internal static async ValueTask<bool> TryStartQuestAsync(
        OfflinePlayer player,
        GameMap map,
        Point giverPosition,
        QuestDefinition quest)
    {
        var npc = FindLiveNpc(map, quest.QuestGiver ?? throw new InvalidOperationException("The quest has no giver."), giverPosition);
        if (npc is null)
        {
            return false;
        }

        await TalkAction.TalkToNpcAsync(player, npc).ConfigureAwait(false);
        if (player.OpenedNpc is null)
        {
            return false;
        }

        try
        {
            await StartAction.StartQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        }
        finally
        {
            await CloseAction.CloseNpcDialogAsync(player).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Hands an active quest in at the giver near the given position. Runs inside the MU Helper tick.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="map">The map the giver stands on.</param>
    /// <param name="giverPosition">The position of the giver.</param>
    /// <param name="quest">The quest to complete.</param>
    /// <returns><c>true</c> if the dialog was opened and the completion was requested.</returns>
    internal static async ValueTask<bool> TryCompleteQuestAsync(
        OfflinePlayer player,
        GameMap map,
        Point giverPosition,
        QuestDefinition quest)
    {
        var npc = FindLiveNpc(map, quest.QuestGiver ?? throw new InvalidOperationException("The quest has no giver."), giverPosition);
        if (npc is null)
        {
            return false;
        }

        await TalkAction.TalkToNpcAsync(player, npc).ConfigureAwait(false);
        if (player.OpenedNpc is null)
        {
            return false;
        }

        try
        {
            await CompletionAction.CompleteQuestAsync(player, quest.Group, quest.Number).ConfigureAwait(false);
        }
        finally
        {
            await CloseAction.CloseNpcDialogAsync(player).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Counts how many items of a requirement the inventory holds, including the required level.
    /// </summary>
    /// <param name="items">The items of the inventory, or <c>null</c>.</param>
    /// <param name="requirement">The item requirement.</param>
    /// <returns>The number of matching items.</returns>
    internal static int CountItem(IEnumerable<Item>? items, QuestItemRequirement requirement)
    {
        if (items is null || requirement.Item is null)
        {
            return 0;
        }

        var requiredLevel = requirement.DropItemGroup?.ItemLevel;
        return items.Count(item => Equals(item.Definition, requirement.Item)
                                   && (requiredLevel is null || item.Level == requiredLevel));
    }

    private static bool HasMetRequirements(Player player, CharacterQuestState state, QuestDefinition quest)
    {
        foreach (var requirement in quest.RequiredItems)
        {
            if (CountItem(player.Inventory?.Items, requirement) < requirement.MinimumNumber)
            {
                return false;
            }
        }

        foreach (var requirement in quest.RequiredMonsterKills)
        {
            var killCount = state.RequirementStates
                .FirstOrDefault(requirementState => Equals(requirementState.Requirement, requirement))
                ?.KillCount ?? 0;
            if (killCount < requirement.MinimumNumber)
            {
                return false;
            }
        }

        return !quest.RequiresClientAction || state.ClientActionPerformed;
    }

    private static (MonsterDefinition Giver, QuestDefinition Quest)[] GetComboQuests(GameConfiguration configuration)
    {
        return ComboQuestCache.GetValue(configuration, static config => config.Monsters
            .Where(giver => giver.NpcWindow == NpcWindow.LegacyQuest)
            .SelectMany(giver => giver.Quests
                .Where(quest => quest.Rewards.Any(reward => reward.RewardType == QuestRewardType.Attribute
                                                            && reward.AttributeReward == Stats.IsSkillComboAvailable))
                .Select(quest => (giver, quest)))
            .ToArray());
    }

    private static async ValueTask<(GameMap Map, Point Position)?> ResolveCachedAsync(
        OfflinePlayer player,
        MonsterDefinition giver,
        CachedGiverLocation cached,
        CancellationToken cancellationToken)
    {
        if (cached.MapNumber is not { } mapNumber)
        {
            return null;
        }

        var map = await player.GameContext.GetMapAsync(mapNumber).ConfigureAwait(false);
        if (map is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FindLiveNpcPosition(map, giver) is { } position ? (map, position) : null;
    }

    private static Point? FindLiveNpcPosition(GameMap map, MonsterDefinition giver)
        => map.GetNpcsInRange(new Point(128, 128), 256)
            .FirstOrDefault(npc => Equals(npc.Definition, giver))
            ?.Position;

    private static NonPlayerCharacter? FindLiveNpc(GameMap map, MonsterDefinition giver, Point near)
        => map.GetNpcsInRange(near, 4)
            .FirstOrDefault(npc => Equals(npc.Definition, giver));

    /// <summary>
    /// What a bot has to hunt for an active quest.
    /// </summary>
    /// <param name="Monsters">The monsters whose kills the quest still requires.</param>
    /// <param name="ItemLevelBands">The monster level bands whose kills can still drop a required quest item.</param>
    internal sealed record FarmTarget(
        IReadOnlyCollection<MonsterDefinition> Monsters,
        IReadOnlyList<(int MinimumLevel, int MaximumLevel)> ItemLevelBands)
    {
        /// <summary>
        /// Gets a value indicating whether the target still needs a specific monster killed.
        /// </summary>
        internal bool NeedsKills => this.Monsters.Count > 0;

        /// <summary>
        /// Gets a value indicating whether the target still needs a quest item.
        /// </summary>
        internal bool NeedsItems => this.ItemLevelBands.Count > 0;

        /// <summary>
        /// Determines whether killing the given monster can advance the quest.
        /// </summary>
        /// <param name="monster">The monster definition.</param>
        /// <returns><c>true</c> if the monster counts for a kill requirement or can drop a required item.</returns>
        internal bool Matches(MonsterDefinition monster)
        {
            if (this.Monsters.Contains(monster))
            {
                return true;
            }

            var level = BotHuntingGroundSelector.GetMonsterLevel(monster);
            return this.ItemLevelBands.Any(band => level >= band.MinimumLevel && level <= band.MaximumLevel);
        }
    }

    /// <summary>
    /// The quest which unlocks the combo, and the giver who offers it.
    /// </summary>
    /// <param name="Giver">The quest giver definition.</param>
    /// <param name="Quest">The next unfinished quest of the chain.</param>
    internal sealed record QuestGoal(MonsterDefinition Giver, QuestDefinition Quest);

    /// <summary>
    /// A shared giver position and the time it was resolved.
    /// </summary>
    /// <param name="AtUtc">When the position was resolved.</param>
    /// <param name="MapNumber">The map number, or <c>null</c> if the giver was not spawned.</param>
    /// <param name="Position">The position, if it was found.</param>
    private sealed record CachedGiverLocation(DateTimeOffset AtUtc, ushort? MapNumber, Point Position);
}
