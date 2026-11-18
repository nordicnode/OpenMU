// <copyright file="BotTownTripTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;
using NUnit.Framework;

/// <summary>
/// Tests for the planning of a town errand - when a bot heads to the NPC which serves it (a merchant or
/// the buff NPC), when it warps into the town of its map first, and when it goes home instead (see
/// <see cref="BotTownTripPlanner"/>).
/// </summary>
/// <remarks>
/// The planner decides the step from plain facts, so the tests state the facts a bot on a map would
/// offer and check which step comes out. The navigator only performs it.
/// </remarks>
[TestFixture]
public class BotTownTripTest
{
    /// <summary>
    /// Where the buff NPC stands, for the tests which have one on the map.
    /// </summary>
    private static readonly Point BuffNpcPosition = new(130, 120);

    /// <summary>
    /// Without a buff NPC configured in the data - or when the bot already has the effect - there is no
    /// trip at all, even though a live NPC may stand nearby.
    /// </summary>
    [Test]
    public void NoTripWithoutAnApplicableBuff()
    {
        var step = BotTownTripPlanner.PlanTrip(
            needsErrand: false,
            npcOnMap: BuffNpcPosition,
            inSafezone: false,
            canWarpToTown: true,
            canWarpHome: true);

        Assert.That(step, Is.EqualTo(BotTownTripPlanner.TripStep.NoErrandNeeded));
    }

    /// <summary>
    /// A bot which stands at the buff NPC - in town, where the NPC stands - walks over to it.
    /// </summary>
    [Test]
    public void BuffNpcInTownIsWalkedTo()
    {
        var step = BotTownTripPlanner.PlanTrip(
            needsErrand: true,
            npcOnMap: BuffNpcPosition,
            inSafezone: true,
            canWarpToTown: true,
            canWarpHome: true);

        Assert.That(step, Is.EqualTo(BotTownTripPlanner.TripStep.WalkToNpc));
    }

    /// <summary>
    /// Out in the field the bot warps into the town of its map first - like a player using a town portal
    /// scroll - and walks to the NPC from there.
    /// </summary>
    [Test]
    public void FieldBuffTripWarpsIntoTheTownFirst()
    {
        var step = BotTownTripPlanner.PlanTrip(
            needsErrand: true,
            npcOnMap: BuffNpcPosition,
            inSafezone: false,
            canWarpToTown: true,
            canWarpHome: false);

        Assert.That(step, Is.EqualTo(BotTownTripPlanner.TripStep.WarpToSafezone));
    }

    /// <summary>
    /// Without a spawn gate there is no town to warp into, so the bot walks - some maps have the NPC near
    /// the spot the bot stands on anyway.
    /// </summary>
    [Test]
    public void BuffNpcWithoutTownsIsWalkedToFromTheField()
    {
        var step = BotTownTripPlanner.PlanTrip(
            needsErrand: true,
            npcOnMap: BuffNpcPosition,
            inSafezone: false,
            canWarpToTown: false,
            canWarpHome: false);

        Assert.That(step, Is.EqualTo(BotTownTripPlanner.TripStep.WalkToNpc));
    }

    /// <summary>
    /// A map without the buff NPC sends the bot home to its class town, where the buff is offered.
    /// </summary>
    [Test]
    public void MissingBuffNpcSendsTheBotHome()
    {
        var step = BotTownTripPlanner.PlanTrip(
            needsErrand: true,
            npcOnMap: null,
            inSafezone: false,
            canWarpToTown: true,
            canWarpHome: true);

        Assert.That(step, Is.EqualTo(BotTownTripPlanner.TripStep.WarpHome));
    }

    /// <summary>
    /// When neither the map nor the hometown offers the NPC, the bot keeps hunting and retries later.
    /// </summary>
    [Test]
    public void MissingBuffNpcWithoutHomeDoesNothing()
    {
        var step = BotTownTripPlanner.PlanTrip(
            needsErrand: true,
            npcOnMap: null,
            inSafezone: true,
            canWarpToTown: true,
            canWarpHome: false);

        Assert.That(step, Is.EqualTo(BotTownTripPlanner.TripStep.NoNpcOnMap));
    }

    /// <summary>
    /// A trip which is already under way keeps walking until the bot stands at the NPC, and claims the
    /// buff only within talking range.
    /// </summary>
    [Test]
    public void TripUnderWayWalksAndThenClaims()
    {
        Assert.That(
            BotTownTripPlanner.PlanTargetStep(distanceToTarget: BotTownTripPlanner.NpcInteractionRange + 1),
            Is.EqualTo(BotTownTripPlanner.TripStep.WalkToNpc));
        Assert.That(
            BotTownTripPlanner.PlanTargetStep(distanceToTarget: BotTownTripPlanner.NpcInteractionRange),
            Is.EqualTo(BotTownTripPlanner.TripStep.Claim));
        Assert.That(
            BotTownTripPlanner.PlanTargetStep(distanceToTarget: 0),
            Is.EqualTo(BotTownTripPlanner.TripStep.Claim));
    }

    /// <summary>
    /// The warp into town uses a spawn gate of the current map, never one of its walk-out gates.
    /// </summary>
    [Test]
    public void TownGateIsASpawnGate()
    {
        var map = CreateMap(0, spawnGates: 1);

        Assert.That(BotTownTripPlanner.FindTownGate(map), Is.EqualTo(map.ExitGates.Single(gate => gate.IsSpawnGate)));
    }

    /// <summary>
    /// A map without a spawn gate gives none - the bot then walks instead of warping.
    /// </summary>
    [Test]
    public void MapWithoutSpawnGateHasNoTownGate()
    {
        var map = CreateMap(0, spawnGates: 0);

        Assert.That(BotTownTripPlanner.FindTownGate(map), Is.Null);
    }

    /// <summary>
    /// The class hometown is the fallback town: an errand which needs one may go there, and its spawn
    /// gate is what the bot warps to.
    /// </summary>
    [Test]
    public void HomeTownIsADestination()
    {
        var home = CreateMap(1, spawnGates: 1);

        var found = BotTownTripPlanner.TryFindHomeGate(
            home,
            currentMapNumber: 2,
            isForbidden: false,
            homeMonsterLevel: 10,
            botLevel: 10,
            allowTrivialHome: true);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Value.Map, Is.SameAs(home));
        Assert.That(found.Value.Gate, Is.EqualTo(home.ExitGates.Single(gate => gate.IsSpawnGate)));
        Assert.That(found.Value.MonsterLevel, Is.EqualTo(10));
    }

    /// <summary>
    /// A high-level bot must not be sent home to farm the starter ground: there it would only find
    /// trivial monsters. An errand which needs a town may still go there.
    /// </summary>
    [Test]
    public void TrivialHometownIsOnlyAllowedForAnErrand()
    {
        var home = CreateMap(1, spawnGates: 1);

        Assert.That(
            BotTownTripPlanner.TryFindHomeGate(home, currentMapNumber: 2, isForbidden: false, homeMonsterLevel: 10, botLevel: 250, allowTrivialHome: false),
            Is.Null,
            "going home is no escape from the ground the bot has to leave");

        Assert.That(
            BotTownTripPlanner.TryFindHomeGate(home, currentMapNumber: 2, isForbidden: false, homeMonsterLevel: 10, botLevel: 250, allowTrivialHome: true),
            Is.Not.Null,
            "an errand which needs a town may still go home");
    }

    /// <summary>
    /// Everything else which makes the hometown no destination: no class hometown, standing on it
    /// already, a forbidden map, or a map without a spawn gate.
    /// </summary>
    [Test]
    public void UnusableHometownIsNoDestination()
    {
        var home = CreateMap(1, spawnGates: 1);

        Assert.That(BotTownTripPlanner.TryFindHomeGate(null, currentMapNumber: 2, isForbidden: false, homeMonsterLevel: 10, botLevel: 10, allowTrivialHome: true), Is.Null, "no home map");
        Assert.That(BotTownTripPlanner.TryFindHomeGate(home, currentMapNumber: 1, isForbidden: false, homeMonsterLevel: 10, botLevel: 10, allowTrivialHome: true), Is.Null, "already there");
        Assert.That(BotTownTripPlanner.TryFindHomeGate(home, currentMapNumber: 2, isForbidden: true, homeMonsterLevel: 10, botLevel: 10, allowTrivialHome: true), Is.Null, "forbidden by the bot settings");
        Assert.That(BotTownTripPlanner.TryFindHomeGate(CreateMap(3, spawnGates: 0), currentMapNumber: 2, isForbidden: false, homeMonsterLevel: 10, botLevel: 10, allowTrivialHome: true), Is.Null, "no spawn gate");
    }

    /// <summary>
    /// Creates a map definition with a walk-out gate and the given number of spawn gates.
    /// </summary>
    /// <param name="number">The map number.</param>
    /// <param name="spawnGates">The number of spawn gates to add.</param>
    /// <returns>The map definition.</returns>
    private static GameMapDefinition CreateMap(byte number, int spawnGates)
    {
        var map = new MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition
        {
            Id = Guid.NewGuid(),
            Number = number,
            TerrainData = new byte[ushort.MaxValue + 3],
        };

        map.ExitGates.Add(CreateGate(map, 10, 10, isSpawnGate: false));
        for (var i = 0; i < spawnGates; i++)
        {
            map.ExitGates.Add(CreateGate(map, (byte)(20 + i), 20, isSpawnGate: true));
        }

        return map;
    }

    private static ExitGate CreateGate(GameMapDefinition map, byte x, byte y, bool isSpawnGate)
        => new MUnique.OpenMU.Persistence.BasicModel.ExitGate
        {
            Id = Guid.NewGuid(),
            Map = map,
            X1 = x,
            X2 = x,
            Y1 = y,
            Y2 = y,
            IsSpawnGate = isSpawnGate,
        };
}
