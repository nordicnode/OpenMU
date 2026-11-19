// <copyright file="BotPartyComposer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Groups bot candidates into goal-driven parties.
/// An experience party holds exactly one buffer and one of each build at most, and all members sit
/// within the level gap above the leader. Event runs, boss attempts and market escorts group by their
/// destination instead: an event party may go without a buffer to be ready as one group, a boss attempt
/// needs a geared vanguard, and an escort is a merchant with a couple of guards.
/// </summary>
internal static class BotPartyComposer
{
    /// <summary>
    /// Composes experience parties from level-ordered candidates.
    /// </summary>
    /// <param name="candidates">The bots without a party, ordered by effective level.</param>
    /// <returns>The formation.</returns>
    internal static Formation Compose(IReadOnlyList<OfflinePlayer> candidates)
        => Compose(candidates, null, DateTime.UtcNow);

    /// <summary>
    /// Composes parties from candidates with their current goals.
    /// </summary>
    /// <param name="candidates">The bots without a party, ordered by effective level.</param>
    /// <param name="intentFor">Reads a bot's goal; <c>null</c> for pure experience grinding.</param>
    /// <param name="now">The current time, against which goals and meetings are checked.</param>
    /// <returns>The formation.</returns>
    internal static Formation Compose(
        IReadOnlyList<OfflinePlayer> candidates,
        Func<OfflinePlayer, BotPartyIntent>? intentFor,
        DateTime now)
    {
        var intents = candidates.ToDictionary(bot => bot, bot => intentFor?.Invoke(bot) ?? BotPartyIntent.Experience);
        var plans = new List<FormedParty>();
        var used = new HashSet<OfflinePlayer>();

        foreach (var group in candidates
                     .Where(bot => intents[bot].Goal == BotPartyGoal.EventRun)
                     .GroupBy(bot => intents[bot].Destination))
        {
            ComposeEventRun(group.ToList(), intents, plans, used, now);
        }

        foreach (var group in candidates
                     .Where(bot => intents[bot].Goal == BotPartyGoal.BossAttempt)
                     .GroupBy(bot => intents[bot].Destination))
        {
            ComposeBossAttempt(group.ToList(), intents, plans, used, now);
        }

        ComposeMarketEscorts(candidates, intents, plans, used, now);

        var waitingBuffers = new List<OfflinePlayer>();
        var experienceCandidates = candidates
            .Where(bot => !used.Contains(bot)
                          && intents[bot].Goal == BotPartyGoal.Experience
                          && BotPartyReadiness.IsAvailable(bot, BotPartyGoal.Experience, now))
            .OrderBy(BotResetHandler.GetEffectiveLevel)
            .ToList();
        ComposeExperience(experienceCandidates, plans, waitingBuffers);

        return new Formation(plans, waitingBuffers);
    }

    /// <summary>
    /// Determines whether a bot-only party no longer fits its goal and should be rebalanced: a member
    /// logged out, the group out-leveled the gap, its goal expired, or its experience composition broke.
    /// Parties with a human are never touched.
    /// </summary>
    /// <param name="party">The party.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c>, if the party should be dissolved so its bots can regroup.</returns>
    internal static bool IsStale(Party party, DateTime now)
    {
        var members = party.PartyList
            .OfType<OfflinePlayer>()
            .Where(bot => bot.Account?.IsBot == true)
            .ToList();
        if (members.Count != party.PartyList.Count)
        {
            return false;
        }

        if (members.Count < BotPartyPolicy.MinPartySize)
        {
            return true;
        }

        var levels = members.Select(BotResetHandler.GetEffectiveLevel).ToList();
        if (levels.Max() - levels.Min() > BotPartyPolicy.MaxLevelGap)
        {
            return true;
        }

        var goal = members[0].PartyGoal;
        if (members.Any(member => member.PartyGoal != goal))
        {
            return true;
        }

        var leaderIntent = BotPartyIntentPlanner.GetIntent(members[0], now);
        return goal switch
        {
            BotPartyGoal.Experience => !IsExperienceCompositionValid(members)
                                       || members.Any(member => BotPartyIntentPlanner.GetIntent(member, now).Goal != BotPartyGoal.Experience),
            BotPartyGoal.EventRun or BotPartyGoal.BossAttempt => leaderIntent.Goal != goal
                                                                  || members.Any(member => BotPartyIntentPlanner.GetIntent(member, now).Destination != leaderIntent.Destination),
            BotPartyGoal.MarketEscort => leaderIntent.Goal != BotPartyGoal.MarketEscort
                                         || members.Skip(1).Any(member => BotPartyIntentPlanner.GetIntent(member, now).Goal != BotPartyGoal.Experience),
            _ => false,
        };
    }

    private static bool IsExperienceCompositionValid(IReadOnlyList<OfflinePlayer> members)
        => members.Count <= BotPartyPolicy.MaxPartySize
           && members.Count(BotBuild.IsSupportElf) == 1
           && members.Select(BotBuild.GetBuildKey).Distinct().Count() == members.Count;

    private static void ComposeExperience(
        List<OfflinePlayer> candidates,
        List<FormedParty> plans,
        List<OfflinePlayer> waitingBuffers)
    {
        while (candidates.Count > 0)
        {
            var leader = candidates[0];
            candidates.RemoveAt(0);

            // Buffers always group up. Others may stay solo for variety.
            if (!BotBuild.IsSupportElf(leader) && Rand.NextInt(0, 100) >= BotPartyPolicy.PartiedSharePercent)
            {
                continue;
            }

            var targetSize = Rand.NextInt(BotPartyPolicy.MinPartySize, BotPartyPolicy.MaxPartySize + 1);
            var members = new List<OfflinePlayer> { leader };
            var picked = PickMembers(candidates, leader, targetSize);

            if (BotBuild.IsSupportElf(leader))
            {
                members.AddRange(picked);
                if (members.Count >= BotPartyPolicy.MinPartySize)
                {
                    RemoveAll(candidates, picked);
                    plans.Add(new FormedParty(BotPartyGoal.Experience, members, BotPartyIntent.Experience));
                }
                else
                {
                    waitingBuffers.Add(leader);
                }

                continue;
            }

            var buffer = FindBuffer(candidates, leader);
            if (buffer is null)
            {
                leader.Logger.LogDebug("Bot '{Name}' stays solo: no buffer in range.", leader.Name);
                continue;
            }

            members.AddRange(picked);
            members.Add(buffer);
            RemoveAll(candidates, picked);
            candidates.Remove(buffer);
            plans.Add(new FormedParty(BotPartyGoal.Experience, members, BotPartyIntent.Experience));
        }
    }

    private static void ComposeEventRun(
        List<OfflinePlayer> candidates,
        IReadOnlyDictionary<OfflinePlayer, BotPartyIntent> intents,
        List<FormedParty> plans,
        HashSet<OfflinePlayer> used,
        DateTime now)
    {
        candidates = candidates.OrderBy(BotResetHandler.GetEffectiveLevel).ToList();
        while (candidates.Count > 0)
        {
            var leader = candidates[0];
            candidates.RemoveAt(0);
            if (!BotPartyReadiness.IsAvailable(leader, BotPartyGoal.EventRun, now)
                || !BotPartyReadiness.HasGearFor(leader, BotPartyGoal.EventRun))
            {
                continue;
            }

            var intent = intents[leader];
            var targetSize = Rand.NextInt(BotPartyPolicy.MinPartySize, BotPartyPolicy.MaxPartySize + 1);
            var members = new List<OfflinePlayer> { leader };
            var leaderLevel = BotResetHandler.GetEffectiveLevel(leader);
            var buildKeys = new HashSet<(byte Line, int Slot)> { BotBuild.GetBuildKey(leader) };
            foreach (var candidate in candidates)
            {
                if (members.Count >= targetSize)
                {
                    break;
                }

                if (BotResetHandler.GetEffectiveLevel(candidate) - leaderLevel > BotPartyPolicy.MaxLevelGap
                    || !BotPartyReadiness.IsAvailable(candidate, BotPartyGoal.EventRun, now)
                    || !BotPartyReadiness.HasGearFor(candidate, BotPartyGoal.EventRun)
                    || !buildKeys.Add(BotBuild.GetBuildKey(candidate)))
                {
                    continue;
                }

                members.Add(candidate);
            }

            if (members.Count < BotPartyPolicy.MinPartySize)
            {
                continue;
            }

            RemoveAll(candidates, members.Skip(1).ToList());
            used.UnionWith(members);
            plans.Add(new FormedParty(BotPartyGoal.EventRun, members, intent));
        }
    }

    private static void ComposeBossAttempt(
        List<OfflinePlayer> candidates,
        IReadOnlyDictionary<OfflinePlayer, BotPartyIntent> intents,
        List<FormedParty> plans,
        HashSet<OfflinePlayer> used,
        DateTime now)
    {
        var remaining = candidates
            .OrderByDescending(BotPartyReadiness.GetReadiness)
            .ToList();
        while (remaining.Count > 0)
        {
            // The front of a boss attempt leads: a geared vanguard if the group has one.
            var leader = remaining.FirstOrDefault(bot => BotPartyReadiness.GetRole(bot) == BotPartyRole.Vanguard)
                         ?? remaining[0];
            remaining.Remove(leader);
            if (!BotPartyReadiness.IsAvailable(leader, BotPartyGoal.BossAttempt, now)
                || !BotPartyReadiness.HasGearFor(leader, BotPartyGoal.BossAttempt))
            {
                continue;
            }

            var intent = intents[leader];
            var members = new List<OfflinePlayer> { leader };
            var leaderLevel = BotResetHandler.GetEffectiveLevel(leader);
            var supports = BotPartyReadiness.GetRole(leader) == BotPartyRole.Support ? 1 : 0;
            foreach (var candidate in remaining)
            {
                if (members.Count >= BotPartyPolicy.MaxPartySize)
                {
                    break;
                }

                if (Math.Abs(BotResetHandler.GetEffectiveLevel(candidate) - leaderLevel) > BotPartyPolicy.MaxLevelGap
                    || !BotPartyReadiness.IsAvailable(candidate, BotPartyGoal.BossAttempt, now)
                    || !BotPartyReadiness.HasGearFor(candidate, BotPartyGoal.BossAttempt))
                {
                    continue;
                }

                var role = BotPartyReadiness.GetRole(candidate);
                if (role == BotPartyRole.Support && supports > 0)
                {
                    continue;
                }

                supports += role == BotPartyRole.Support ? 1 : 0;
                members.Add(candidate);
            }

            if (members.Count < BotPartyPolicy.MinPartySize)
            {
                continue;
            }

            var memberSet = members.ToHashSet();
            remaining.RemoveAll(memberSet.Contains);
            used.UnionWith(members);
            plans.Add(new FormedParty(BotPartyGoal.BossAttempt, members, intent));
        }
    }

    private static void ComposeMarketEscorts(
        IReadOnlyList<OfflinePlayer> candidates,
        IReadOnlyDictionary<OfflinePlayer, BotPartyIntent> intents,
        List<FormedParty> plans,
        HashSet<OfflinePlayer> used,
        DateTime now)
    {
        var merchants = candidates
            .Where(bot => intents[bot].Goal == BotPartyGoal.MarketEscort)
            .OrderByDescending(BotResetHandler.GetEffectiveLevel)
            .ToList();
        if (merchants.Count == 0)
        {
            return;
        }

        var guards = candidates
            .Where(bot => !used.Contains(bot)
                          && intents[bot].Goal == BotPartyGoal.Experience
                          && BotPartyReadiness.IsAvailable(bot, BotPartyGoal.MarketEscort, now)
                          && BotPartyReadiness.HasGearFor(bot, BotPartyGoal.MarketEscort))
            .ToList();
        foreach (var merchant in merchants)
        {
            if (used.Contains(merchant))
            {
                continue;
            }

            var merchantLevel = BotResetHandler.GetEffectiveLevel(merchant);
            var members = new List<OfflinePlayer> { merchant };
            foreach (var guard in guards)
            {
                if (members.Count > BotPartyPolicy.MarketEscortGuards)
                {
                    break;
                }

                if (used.Contains(guard)
                    || BotPartyReadiness.GetRole(guard) == BotPartyRole.Support
                    || Math.Abs(BotResetHandler.GetEffectiveLevel(guard) - merchantLevel) > BotPartyPolicy.MaxLevelGap)
                {
                    continue;
                }

                members.Add(guard);
            }

            if (members.Count < BotPartyPolicy.MinPartySize)
            {
                continue;
            }

            used.UnionWith(members);
            plans.Add(new FormedParty(BotPartyGoal.MarketEscort, members, intents[merchant]));
        }
    }

    private static List<OfflinePlayer> PickMembers(List<OfflinePlayer> remaining, OfflinePlayer leader, int targetSize)
    {
        var picked = new List<OfflinePlayer>();
        var leaderLevel = BotResetHandler.GetEffectiveLevel(leader);
        var openSlots = (BotBuild.IsSupportElf(leader) ? targetSize : targetSize - 1) - 1;
        var buildKeys = new HashSet<(byte Line, int Slot)> { BotBuild.GetBuildKey(leader) };

        foreach (var candidate in remaining)
        {
            if (picked.Count >= openSlots)
            {
                break;
            }

            if (BotResetHandler.GetEffectiveLevel(candidate) - leaderLevel > BotPartyPolicy.MaxLevelGap)
            {
                break;
            }

            // Buffers join through FindBuffer. One of each build at most.
            if (BotBuild.IsSupportElf(candidate) || !buildKeys.Add(BotBuild.GetBuildKey(candidate)))
            {
                continue;
            }

            picked.Add(candidate);
        }

        return picked;
    }

    private static OfflinePlayer? FindBuffer(List<OfflinePlayer> remaining, OfflinePlayer leader)
    {
        var leaderLevel = BotResetHandler.GetEffectiveLevel(leader);
        return remaining
            .Where(BotBuild.IsSupportElf)
            .Where(b => Math.Abs(BotResetHandler.GetEffectiveLevel(b) - leaderLevel) <= BotPartyPolicy.MaxLevelGap)
            .MinBy(b => Math.Abs(BotResetHandler.GetEffectiveLevel(b) - leaderLevel));
    }

    private static void RemoveAll(List<OfflinePlayer> remaining, List<OfflinePlayer> picked)
    {
        foreach (var member in picked)
        {
            remaining.Remove(member);
        }
    }

    /// <summary>
    /// One formed party and why it exists.
    /// </summary>
    /// <param name="Goal">The goal of the party.</param>
    /// <param name="Members">The members; the first one is the leader.</param>
    /// <param name="Intent">The destination of the goal.</param>
    internal sealed record FormedParty(
        BotPartyGoal Goal,
        IReadOnlyList<OfflinePlayer> Members,
        BotPartyIntent Intent);

    /// <summary>
    /// The outcome of one formation pass.
    /// </summary>
    /// <param name="Plans">The formed parties with their goals.</param>
    /// <param name="UnplacedBuffers">Buffers which found no experience party and hunt solo.</param>
    internal sealed record Formation(
        IReadOnlyList<FormedParty> Plans,
        IReadOnlyList<OfflinePlayer> UnplacedBuffers)
    {
        /// <summary>
        /// Gets the member lists of the formed parties.
        /// </summary>
        internal IReadOnlyList<IReadOnlyList<OfflinePlayer>> Parties
            => this.Plans.Select(plan => plan.Members).ToList();
    }
}
