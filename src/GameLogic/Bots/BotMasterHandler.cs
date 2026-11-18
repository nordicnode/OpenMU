// <copyright file="BotMasterHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;

/// <summary>
/// Handles the third-generation ("master") stage of a bot's career: the evolution into the master
/// class at the game's maximum level - the same class assignment the level-400 master quests perform
/// for a human player - and the investment of the master points earned per master level, through the
/// regular <see cref="AddMasterPointAction"/> with all its validations.
/// </summary>
/// <remarks>
/// On servers with the reset feature the evolution follows one iron rule: a bot only becomes a master
/// when no reset can ever follow, i.e. the configured reset limit is exhausted. While resets remain -
/// or when no limit is configured at all, so resetting forever is the endgame - the bot keeps resetting
/// and never masters, like the players of such servers. Without the reset feature it evolves as soon as
/// it reaches the maximum level.
/// The master class's base attributes (master experience rate, master points per level) and the master
/// level stat are only mounted into the attribute system when the character enters the world, so the
/// caller must restart the bot after the evolution (see <c>BotManager.RestartBotAsync</c>) - the ghost
/// equivalent of a player relogging; master experience only flows after that fresh world entry.
/// </remarks>
internal static class BotMasterHandler
{
    /// <summary>
    /// A master skill unlocks the next rank of its root (and satisfies "required skill" links) at this
    /// level, see <see cref="AddMasterPointAction"/>.
    /// </summary>
    private const int RankUnlockLevel = 10;

    /// <summary>The item groups of the weapons a master bonus can be tied to (see <see cref="WeaponGroupOfBonus"/>).</summary>
    private const byte SwordGroup = 0;

    /// <summary>The item group of the maces - the scepters live here as well.</summary>
    private const byte MaceGroup = 2;

    /// <summary>The item group of the spears.</summary>
    private const byte SpearGroup = 3;

    /// <summary>The item group of the bows and crossbows.</summary>
    private const byte BowGroup = 4;

    /// <summary>The item group of the staffs - the sticks and books live here as well.</summary>
    private const byte StaffGroup = 5;

    private static readonly AddMasterPointAction AddPointAction = new();

    /// <summary>
    /// Determines whether the bot is due for its master evolution: it has a master class to evolve
    /// into, stands at the game's maximum level, and - on reset servers - exhausted the reset limit
    /// (see the remarks of <see cref="BotMasterHandler"/> for the rationale).
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <returns>True, if the bot should evolve into its master class now.</returns>
    public static bool IsMasterEvolutionDue(Player player)
    {
        if (player.SelectedCharacter is not { CharacterClass: { } currentClass }
            || player.Attributes is not { } attributes
            || BotProgression.GetMasterEvolutionTarget(currentClass) is null)
        {
            return false;
        }

        if ((int)attributes[Stats.Level] < player.GameContext.Configuration.MaximumLevel)
        {
            return false;
        }

        if (BotResetHandler.GetResetConfiguration(player.GameContext) is { } resetConfiguration
            && (resetConfiguration.ResetLimit is not > 0
                || (int)attributes[Stats.Resets] < resetConfiguration.ResetLimit))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Evolves the bot into its master class when due. The caller must restart the bot afterwards
    /// (see the remarks of <see cref="BotMasterHandler"/>).
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <returns>True, if the evolution was performed.</returns>
    public static async ValueTask<bool> TryEvolveAsync(OfflinePlayer player)
    {
        if (!IsMasterEvolutionDue(player) || player.SelectedCharacter is not { } character)
        {
            return false;
        }

        var masterClass = BotProgression.GetMasterEvolutionTarget(character.CharacterClass!)!;
        character.CharacterClass = masterClass;
        player.Logger.LogDebug(
            "Bot '{Name}' evolved into master class {Class} at level {Level}.",
            player.Name,
            masterClass.Name,
            player.Level);

        try
        {
            // Persist right away like a performed reset - the following restart reloads the character
            // from the database, so the class change must be down there before it.
            await player.SaveProgressAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger.LogWarning(ex, "Couldn't save bot '{Name}' right after its master evolution; the logout save will retry.", player.Name);
        }

        return true;
    }

    /// <summary>
    /// Determines whether the bot is a master with points to invest - the cheap per-tick guard for
    /// queueing <see cref="TrySpendMasterPointsAsync"/>.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <returns>True, if there are master points to spend.</returns>
    public static bool HasMasterPointsToSpend(Player player)
        => player.SelectedCharacter is { CharacterClass.IsMasterClass: true, MasterLevelUpPoints: > 0 };

    /// <summary>
    /// Invests the bot's available master points (earned one per master level) into its master skill
    /// tree through the regular <see cref="AddMasterPointAction"/>. Learning a skill mutates the
    /// skill list, so this must run inside the bot's AI tick - queue it via
    /// <see cref="OfflinePlayer.PendingBotActions"/> (see the call site in <c>BotNavigator</c>);
    /// with no points available it is a cheap no-op.
    /// Points are usually invested as they are earned, but the policy can bank them up to
    /// <see cref="BotConfiguration.GetEffectiveMasterPointBankingCap"/> when the next worthwhile skill
    /// of the build costs more than the bot currently holds (see <see cref="ShouldBankPoints"/>).
    /// Without that, a master skill whose minimum level is ten points could never be learned by a bot:
    /// it would have spent every single point before the tenth arrived.
    /// </summary>
    /// <param name="player">The bot player.</param>
    public static async ValueTask TrySpendMasterPointsAsync(OfflinePlayer player)
    {
        if (player.SelectedCharacter is not { CharacterClass.IsMasterClass: true } character
            || character.MasterLevelUpPoints < 1)
        {
            return;
        }

        var bankCap = BotFeaturePlugIn.GetConfiguration(player.GameContext)?.GetEffectiveMasterPointBankingCap() ?? 0;
        while (character.MasterLevelUpPoints > 0)
        {
            if (ShouldBankPoints(player, bankCap))
            {
                player.Logger.LogDebug(
                    "Bot '{Name}' banks its {Points} master point(s) for a more expensive skill.",
                    player.Name,
                    character.MasterLevelUpPoints);
                return;
            }

            if (PickNextMasterSkill(player) is not { } skill)
            {
                return;
            }

            var pointsBefore = character.MasterLevelUpPoints;
            await AddPointAction.AddMasterPointAsync(player, (ushort)skill.Number).ConfigureAwait(false);
            if (character.MasterLevelUpPoints >= pointsBefore)
            {
                // The action refused - its own checks are authoritative, don't loop on the same pick.
                break;
            }

            player.Logger.LogDebug(
                "Bot '{Name}' invested {Points} master point(s) into '{Skill}'.",
                player.Name,
                pointsBefore - character.MasterLevelUpPoints,
                skill.Name);
        }
    }

    /// <summary>
    /// Determines whether the bot should hold its earned master points instead of spending them now.
    /// It does when a skill its build wants becomes learnable only with more points than it holds, and
    /// banking those points is allowed (<paramref name="bankCap"/> is the most it will hold, 0 disables
    /// banking entirely). The cap is the safety fallback: if the wanted skill costs more than the cap,
    /// the bot spends its points normally instead of hoarding forever.
    /// Pure decision logic - exposed for unit tests.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="bankCap">The maximum number of master points to bank; 0 disables banking.</param>
    /// <returns>True, if the points should be kept for later.</returns>
    internal static bool ShouldBankPoints(Player player, int bankCap)
    {
        if (bankCap <= 0 || player.SelectedCharacter is not { CharacterClass.IsMasterClass: true } character)
        {
            return false;
        }

        var available = character.MasterLevelUpPoints;
        if (available >= bankCap || HasUnfinishedGateSkill(character.LearnedSkills))
        {
            return false;
        }

        // Learning an affordable skill beats saving points for a later one.
        if (PickNextMasterSkill(player) is { } next && character.LearnedSkills.All(l => l.Skill != next))
        {
            return false;
        }

        return PickBankingTarget(player, available, bankCap) is not null;
    }

    /// <summary>
    /// Picks the skill the bot would bank its points for: the best new skill of its build which the
    /// tree allows, but which needs more points at once than the bot holds (a minimum level above
    /// <paramref name="availablePoints"/>), while still being reachable within <paramref name="bankCap"/>.
    /// Pure decision logic - exposed for unit tests.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="availablePoints">The master points the bot currently holds.</param>
    /// <param name="bankCap">The maximum number of points the bot may bank.</param>
    /// <returns>The skill to bank for, or <c>null</c> when there is none.</returns>
    internal static Skill? PickBankingTarget(Player player, int availablePoints, int bankCap)
    {
        if (player.SelectedCharacter is not { CharacterClass: { } characterClass } character
            || player.SkillList is not { } skillList)
        {
            return null;
        }

        return player.GameContext.Configuration.Skills
            .Where(s => s.MasterDefinition is { } definition
                        && definition.Root is not null
                        && s.QualifiedCharacters.Contains(characterClass)
                        && character.LearnedSkills.All(l => l.Skill != s)
                        && definition.MinimumLevel > availablePoints
                        && definition.MinimumLevel <= bankCap
                        && CanLearn(player, s, int.MaxValue))
            .OrderBy(s => GetPickTier(player, s, skillList))
            .ThenByDescending(s => GetBuildAffinity(player, s))
            .ThenBy(s => s.MasterDefinition!.MinimumLevel)
            .ThenBy(s => s.MasterDefinition!.Rank)
            .ThenBy(s => s.Number)
            .FirstOrDefault();
    }

    /// <summary>
    /// Picks the master skill the bot invests its next point into, or <c>null</c> when nothing is
    /// eligible. The policy fills the tree like a player: first push a started skill to the rank-unlock
    /// level of 10, then learn a new eligible skill - preferring "useful" ones, i.e. passives boosting a
    /// stat or strengtheners of a skill the bot actually has - and finally pump the learned skills
    /// towards their maximum. Deterministic, so a bot builds the same tree across sessions.
    /// Pure decision logic - exposed for unit tests.
    /// </summary>
    /// <param name="player">The bot player.</param>
    internal static Skill? PickNextMasterSkill(Player player)
    {
        if (player.SelectedCharacter is not { CharacterClass: { } characterClass } character
            || player.SkillList is not { } skillList)
        {
            return null;
        }

        var learned = character.LearnedSkills
            .Where(l => l.Skill?.MasterDefinition?.Root is not null)
            .ToList();

        if (learned
                .Where(l => l.Level < RankUnlockLevel && l.Level < l.Skill!.MasterDefinition!.MaximumLevel)
                .OrderBy(l => l.Skill!.MasterDefinition!.Rank)
                .ThenBy(l => l.Skill!.Number)
                .FirstOrDefault() is { } gate)
        {
            return gate.Skill;
        }

        if (player.GameContext.Configuration.Skills
                .Where(s => s.MasterDefinition?.Root is not null
                            && s.QualifiedCharacters.Contains(characterClass)
                            && character.LearnedSkills.All(l => l.Skill != s)
                            && CanLearn(player, s, character.MasterLevelUpPoints))
                .OrderBy(s => GetPickTier(player, s, skillList))
                .ThenByDescending(s => GetBuildAffinity(player, s))
                .ThenBy(s => s.MasterDefinition!.MinimumLevel)
                .ThenBy(s => s.MasterDefinition!.Rank)
                .ThenBy(s => s.Number)
                .FirstOrDefault() is { } newSkill)
        {
            return newSkill;
        }

        return learned
            .Where(l => l.Level < l.Skill!.MasterDefinition!.MaximumLevel)
            .OrderBy(l => GetPickTier(player, l.Skill!, skillList))
            .ThenByDescending(l => GetBuildAffinity(player, l.Skill!))
            .ThenBy(l => l.Skill!.MasterDefinition!.Rank)
            .ThenBy(l => l.Skill!.Number)
            .FirstOrDefault()?.Skill;
    }

    /// <summary>
    /// Whether a learned skill still waits for its rank-unlock level; such a skill is always finished
    /// first, like a player completes a branch before opening the next one.
    /// </summary>
    /// <param name="learnedSkills">The learned skills of the character.</param>
    private static bool HasUnfinishedGateSkill(IEnumerable<SkillEntry> learnedSkills)
    {
        return learnedSkills.Any(l => l.Skill?.MasterDefinition is { } definition
                                      && definition.Root is not null
                                      && l.Level < RankUnlockLevel
                                      && l.Level < definition.MaximumLevel);
    }

    /// <summary>
    /// The priority tier of a pick, lowest first: a strengthener/mastery of a skill the bot actually
    /// fights with improves what it does every second, a passive which helps regardless of the target
    /// comes next, and everything else (a bonus for a weapon the bot does not carry, a PvP-only bonus)
    /// is filled last. Prevents a bot from spending its first points on something it can never use.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="skill">The candidate skill.</param>
    /// <param name="skillList">The bot's skill list.</param>
    private static int GetPickTier(Player player, Skill skill, ISkillList skillList)
    {
        var definition = skill.MasterDefinition!;
        if (definition.ReplacedSkill is { } replaced && skillList.ContainsSkill((ushort)replaced.Number))
        {
            return 0;
        }

        if (definition.TargetAttribute is not { } target || IsPvpOnlyBonus(target))
        {
            return 2;
        }

        return WeaponGroupOfBonus(target) is { } weaponGroup && !CarriesWeaponOfGroup(player, weaponGroup)
            ? 2
            : 1;
    }

    /// <summary>
    /// How well a master bonus fits the bot's own build: a bonus of the damage channel the build
    /// actually fights with counts full, a defensive or utility bonus half, and everything else
    /// nothing. Used as the tie-breaker among equally prioritized picks, so e.g. an energy build
    /// prefers its wizardry bonus over a physical one instead of taking whatever comes first by number.
    /// </summary>
    /// <param name="player">The bot player.</param>
    /// <param name="skill">The candidate skill.</param>
    private static int GetBuildAffinity(Player player, Skill skill)
    {
        if (skill.MasterDefinition?.TargetAttribute is not { } target
            || player.SelectedCharacter is not { CharacterClass: { } characterClass } character)
        {
            return 0;
        }

        if (BotProgression.TotalToBaseStat(target) is { } stat)
        {
            return BotProgression.GetStatWeights(characterClass, character.Name)
                .Where(w => w.Stat == stat)
                .Select(w => w.Weight)
                .FirstOrDefault();
        }

        if (IsWizardryOrCurseBonus(target))
        {
            return BotProgression.GetStatWeights(characterClass, character.Name).First().Stat == Stats.BaseEnergy
                ? 20
                : 0;
        }

        if (IsPhysicalBonus(target))
        {
            return BotProgression.GetStatWeights(characterClass, character.Name).First().Stat == Stats.BaseEnergy
                ? 0
                : 20;
        }

        return 10;
    }

    private static bool IsWizardryOrCurseBonus(AttributeDefinition attribute)
    {
        return attribute == Stats.WizardryBaseDmg
               || attribute == Stats.CurseBaseDmg
               || attribute == Stats.MinimumWizBaseDmg
               || attribute == Stats.MaximumWizBaseDmg
               || attribute == Stats.MinimumCurseBaseDmg
               || attribute == Stats.MaximumCurseBaseDmg;
    }

    private static bool IsPhysicalBonus(AttributeDefinition attribute)
    {
        return attribute == Stats.OneHandedSwordBonusDamage
               || attribute == Stats.TwoHandedSwordStrBonusDamage
               || attribute == Stats.TwoHandedSwordMasteryBonusDamage
               || attribute == Stats.GloveWeaponBonusDamage
               || attribute == Stats.MaceBonusDamage
               || attribute == Stats.MaceMasteryStunChance
               || attribute == Stats.ScepterStrBonusDamage
               || attribute == Stats.ScepterMasteryBonusDamage
               || attribute == Stats.ScepterPetBonusDamage
               || attribute == Stats.BonusDamageWithScepterCmdDiv
               || attribute == Stats.SpearBonusDamage
               || attribute == Stats.SpearMasteryDoubleDamageChance
               || attribute == Stats.BowStrBonusDamage
               || attribute == Stats.CrossBowStrBonusDamage
               || attribute == Stats.CrossBowMasteryBonusDamage
               || attribute == Stats.OneHandedStaffBonusBaseDamage
               || attribute == Stats.TwoHandedStaffBonusBaseDamage
               || attribute == Stats.TwoHandedStaffMasteryBonusDamage
               || attribute == Stats.StickBonusBaseDamage
               || attribute == Stats.StickMasteryBonusDamage
               || attribute == Stats.BookBonusBaseDamage;
    }

    /// <summary>
    /// Mirrors the private requisition checks of <see cref="AddMasterPointAction"/> (minimum points,
    /// previous rank of the same root at 10+, required skills), so the picker only proposes skills the
    /// action will accept. A mismatch is harmless: the action refuses and the spend loop stops.
    /// </summary>
    private static bool CanLearn(Player player, Skill skill, int availablePoints)
    {
        var definition = skill.MasterDefinition!;
        if (availablePoints < definition.MinimumLevel)
        {
            return false;
        }

        if (definition.Rank > 1
            && !player.SelectedCharacter!.LearnedSkills.Any(l =>
                l.Skill?.MasterDefinition?.Root is { } root
                && root.Equals(definition.Root)
                && l.Skill.MasterDefinition.Rank == definition.Rank - 1
                && l.Level >= RankUnlockLevel))
        {
            return false;
        }

        if (definition.RequiredMasterSkills?.Any() == true
            && !definition.RequiredMasterSkills.All(s =>
                player.SelectedCharacter!.LearnedSkills.Any(l => l.Skill == s && l.Level >= RankUnlockLevel)
                || (s.MasterDefinition is null && player.SkillList?.ContainsSkill((ushort)s.Number) == true)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the master bonus only ever applies in a fight against another player, which is not what a
    /// bot's points are for.
    /// </summary>
    /// <param name="attribute">The bonus attribute.</param>
    private static bool IsPvpOnlyBonus(AttributeDefinition attribute)
    {
        return attribute == Stats.AttackRatePvp || attribute == Stats.DefenseRatePvp;
    }

    /// <summary>
    /// The item group of the weapon a master bonus attribute belongs to, or <c>null</c> when the bonus
    /// helps regardless of the weapon (health, defense, an attack rate, ...). The item groups come from
    /// the item data: scepters live in the mace group, the Rage Fighter's gloves in the sword group, and
    /// sticks and books next to the staffs.
    /// </summary>
    private static byte? WeaponGroupOfBonus(AttributeDefinition attribute)
    {
        if (attribute == Stats.OneHandedSwordBonusDamage
            || attribute == Stats.TwoHandedSwordStrBonusDamage
            || attribute == Stats.TwoHandedSwordMasteryBonusDamage
            || attribute == Stats.GloveWeaponBonusDamage)
        {
            return SwordGroup;
        }

        if (attribute == Stats.MaceBonusDamage
            || attribute == Stats.MaceMasteryStunChance
            || attribute == Stats.ScepterStrBonusDamage
            || attribute == Stats.ScepterMasteryBonusDamage
            || attribute == Stats.ScepterPetBonusDamage
            || attribute == Stats.BonusDamageWithScepterCmdDiv)
        {
            return MaceGroup;
        }

        if (attribute == Stats.SpearBonusDamage
            || attribute == Stats.SpearMasteryDoubleDamageChance)
        {
            return SpearGroup;
        }

        if (attribute == Stats.BowStrBonusDamage
            || attribute == Stats.CrossBowStrBonusDamage
            || attribute == Stats.CrossBowMasteryBonusDamage)
        {
            return BowGroup;
        }

        if (attribute == Stats.OneHandedStaffBonusBaseDamage
            || attribute == Stats.TwoHandedStaffBonusBaseDamage
            || attribute == Stats.TwoHandedStaffMasteryBonusDamage
            || attribute == Stats.StickBonusBaseDamage
            || attribute == Stats.StickMasteryBonusDamage
            || attribute == Stats.BookBonusBaseDamage)
        {
            return StaffGroup;
        }

        return null;
    }

    /// <summary>
    /// Whether the bot fights with a weapon of this item group - what it carries right now, and what its
    /// build makes it pick up in the future (see <see cref="BotProgression.IsPreferredWeaponGroup"/>), so
    /// a bot which is momentarily unarmed does not start collecting bonuses for the wrong weapon.
    /// </summary>
    private static bool CarriesWeaponOfGroup(Player player, byte weaponGroup)
    {
        if (player.Inventory?.GetItem(InventoryConstants.LeftHandSlot)?.Definition is { } weapon
            && weapon.Group == weaponGroup)
        {
            return true;
        }

        return player.SelectedCharacter is { CharacterClass: { } characterClass } character
               && BotProgression.IsPreferredWeaponGroup(characterClass, character.Name, weaponGroup);
    }
}
