// <copyright file="BotTickOrderTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;
using NUnit.Framework;

/// <summary>
/// Tests for the priority order a navigation tick walks - the declared order of the steps in
/// <see cref="BotNavigator"/> (see <see cref="BotNavigator.TickStepOrder"/>).
/// </summary>
/// <remarks>
/// The order is a contract, not an implementation detail: which mode wins a tick decides whether a bot
/// finishes its errand, follows its leader or hunts, and a silently reordered table changes the behaviour of
/// every bot. Pinning it here makes such a change visible in a review instead of in a running server.
/// </remarks>
[TestFixture]
public class BotTickOrderTest
{
    /// <summary>
    /// The steps run in exactly this order: an announced event meeting first, then a boss attempt, the market
    /// shift and a due reset, the upkeep, the town errands, the revenge march, following the leader, the warp to
    /// a better map - and the routine last, which is what happens when nothing else claimed the tick.
    /// </summary>
    [Test]
    public void TheTickTriesTheModesInTheirPriorityOrder()
    {
        Assert.That(
            BotNavigator.TickStepOrder,
            Is.EqualTo(new BotState?[]
            {
                BotState.EventMeeting,
                BotState.BossAttempt,
                BotState.MarketShift,
                BotState.Reset,
                null, // the master point upkeep, which claims no state
                BotState.Shopping,
                BotState.Buff,
                BotState.ChaosMachine,
                BotState.Revenge,
                BotState.FollowLeader, // to the leader's map
                BotState.FollowLeader, // around the leader
                BotState.Travel, // warping to a better map
                null, // the routine, which claims its own state
            }));
    }

    /// <summary>
    /// Every errand runs before the party follow and the routine: a bot which is in the middle of a trip must
    /// finish it, instead of being pulled back to its leader or picking a hunting ground.
    /// </summary>
    [Test]
    public void EveryErrandOutranksFollowingTheLeaderAndTheRoutine()
    {
        var order = BotNavigator.TickStepOrder.ToList();
        var followIndex = order.IndexOf(BotState.FollowLeader);
        var routineIndex = order.Count - 1;

        foreach (var errand in new[] { BotState.EventMeeting, BotState.BossAttempt, BotState.MarketShift, BotState.Reset, BotState.Shopping, BotState.Buff, BotState.ChaosMachine, BotState.Revenge })
        {
            var errandIndex = order.IndexOf(errand);
            Assert.That(errandIndex, Is.GreaterThanOrEqualTo(0), $"{errand} is not part of the tick");
            Assert.That(errandIndex, Is.LessThan(followIndex), $"{errand} must run before following the leader");
            Assert.That(errandIndex, Is.LessThan(routineIndex), $"{errand} must run before the routine");
        }
    }

    /// <summary>
    /// Every mode is claimed by a step or by a check before the steps, so no state of the bot can exist which
    /// nothing ever reports - a dead mode would otherwise only show up as "never entered" on a live server.
    /// </summary>
    [Test]
    public void EveryModeIsClaimedSomewhere()
    {
        var declared = BotNavigator.DeclaredClaims;
        var all = Enum.GetValues<BotState>();

        var unclaimed = all.Where(state => !declared.Contains(state)).ToList();
        Assert.That(unclaimed, Is.Empty, "no step claims: " + string.Join(", ", unclaimed));
        Assert.That(declared, Has.Count.EqualTo(all.Length), "the claims name a state which no longer exists");
    }

    /// <summary>
    /// A step which reports a state also declares it as one of its claims, so the declared set cannot drift
    /// away from what the steps actually do.
    /// </summary>
    [Test]
    public void AStepWhichReportsAStateAlsoDeclaresIt()
    {
        foreach (var (state, claims) in BotNavigator.TickStepClaims)
        {
            if (state is { } reported)
            {
                Assert.That(claims, Does.Contain(reported), $"the step reporting {reported} does not declare it");
            }
        }
    }

    /// <summary>
    /// The routine claims the states it computes itself, which is why it is the one step without a fixed
    /// state in the order.
    /// </summary>
    [Test]
    public void TheRoutineDeclaresTheStatesItComputes()
    {
        var routine = BotNavigator.TickStepClaims[^1];

        Assert.That(routine.State, Is.Null);
        Assert.That(routine.Claims, Is.EquivalentTo(new[] { BotState.Hunt, BotState.Travel, BotState.Idle }));
    }
}
