// <copyright file="BotEventMeetingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests where a bot waits for players after it announced an opened event
/// (<see cref="BotEventMeetingPlanner"/>).
/// </summary>
[TestFixture]
public class BotEventMeetingTest
{
    private static readonly Point Npc = new(100, 100);

    /// <summary>
    /// The NPC's own tile is usually blocked, and a route to a blocked tile does not exist - the bot
    /// stands on the nearest walkable tile next to it.
    /// </summary>
    [Test]
    public void StandsOnTheNearestWalkableTileNextToTheNpc()
    {
        var terrain = CreateBlockedTerrain();
        terrain.WalkMap[Npc.X, Npc.Y] = true;
        terrain.WalkMap[Npc.X + 2, Npc.Y] = true;
        terrain.WalkMap[Npc.X + 1, Npc.Y + 1] = true;

        var point = BotEventMeetingPlanner.FindStandingPoint(terrain, Npc, 3);

        Assert.That(point, Is.EqualTo(new Point((byte)(Npc.X + 1), (byte)(Npc.Y + 1))));
    }

    /// <summary>
    /// The meeting happens in town: a walkable tile in the safezone wins over a nearer one outside.
    /// </summary>
    [Test]
    public void PrefersTheSafezone()
    {
        var terrain = CreateBlockedTerrain();
        terrain.WalkMap[Npc.X + 1, Npc.Y] = true;
        terrain.WalkMap[Npc.X + 3, Npc.Y] = true;
        terrain.SafezoneMap[Npc.X + 3, Npc.Y] = true;

        var point = BotEventMeetingPlanner.FindStandingPoint(terrain, Npc, 3);

        Assert.That(point, Is.EqualTo(new Point((byte)(Npc.X + 3), Npc.Y)));
    }

    /// <summary>
    /// Without any walkable tile in range there is no standing point - the bot gives up the meeting.
    /// </summary>
    [Test]
    public void NoStandingPointWithoutWalkableTiles()
    {
        var terrain = CreateBlockedTerrain();

        Assert.That(BotEventMeetingPlanner.FindStandingPoint(terrain, Npc, 3), Is.Null);
    }

    /// <summary>
    /// Chaos Castle has no entry NPC: the bot waits in the safezone of its home town.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask ChaosCastleMeetsInTheHomeTownAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        var homeTown = new GameMapDefinition { Number = 3 };
        Mock.Get(bot.SelectedCharacter!.CharacterClass!).Setup(c => c.HomeMap).Returns(homeTown);
        var untilUtc = new DateTime(2026, 11, 4, 20, 5, 0, DateTimeKind.Utc);

        var meeting = BotEventMeetingPlanner.TryPlan(bot, MiniGameType.ChaosCastle, untilUtc);

        Assert.That(meeting, Is.EqualTo(new BotEventMeeting(homeTown, null, untilUtc)));
    }

    /// <summary>
    /// The entry NPC waits on a floor which isn't connected to the map's spawn gate by walkable ground:
    /// the bot plans the warp to that floor. Otherwise it would promise a meeting at the spawn floor and
    /// only find out there that it can't walk to the NPC.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask EntersTheFloorOfTheEntryNpcAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        bot.Attributes![Stats.Level] = 100;

        var terrain = CreateBlockedTerrainData();
        MarkWalkableArea(terrain, 10, 10, 12, 12);
        MarkWalkableArea(terrain, 200, 200, 202, 202);
        var map = CreateMap(4, terrain);
        gameContext.Configuration.Maps.Add(map);
        var lowerGate = CreateGate(map, 10, 10, 11, 11);
        lowerGate.IsSpawnGate = true;
        var upperGate = CreateGate(map, 200, 200, 201, 201);
        gameContext.Configuration.WarpList.Add(new MUnique.OpenMU.Persistence.BasicModel.WarpInfo { Id = Guid.NewGuid(), Name = "LostTower", Gate = lowerGate, LevelRequirement = 50 });
        gameContext.Configuration.WarpList.Add(new MUnique.OpenMU.Persistence.BasicModel.WarpInfo { Id = Guid.NewGuid(), Name = "LostTower7", Gate = upperGate, LevelRequirement = 70 });
        AddEntryNpc(map, 200, 200, NpcWindow.BloodCastle);

        var meeting = BotEventMeetingPlanner.TryPlan(bot, MiniGameType.BloodCastle, DateTime.UtcNow.AddMinutes(5));

        Assert.That(meeting, Is.Not.Null);
        Assert.That(meeting!.Map, Is.SameAs(map));
        Assert.That(meeting.NpcPosition, Is.EqualTo(new Point(200, 200)));
        Assert.That(meeting.Gate, Is.SameAs(upperGate));
        Assert.That(meeting.State, Is.EqualTo(BotMeetingState.Announced));
    }

    /// <summary>
    /// No walkable tile next to the entry NPC: the bot stays home instead of announcing a meeting at
    /// which nobody would find it.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask StaysHomeWhenTheEntryNpcCantBeWaitedAtAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        bot.Attributes![Stats.Level] = 100;

        var map = CreateMap(4, CreateBlockedTerrainData());
        gameContext.Configuration.Maps.Add(map);
        var gate = CreateGate(map, 10, 10, 11, 11);
        gameContext.Configuration.WarpList.Add(new MUnique.OpenMU.Persistence.BasicModel.WarpInfo { Id = Guid.NewGuid(), Name = "LostTower", Gate = gate, LevelRequirement = 50 });
        AddEntryNpc(map, 100, 100, NpcWindow.BloodCastle);

        Assert.That(BotEventMeetingPlanner.TryPlan(bot, MiniGameType.BloodCastle, DateTime.UtcNow.AddMinutes(5)), Is.Null);
    }

    /// <summary>
    /// A meeting ends in exactly one terminal state: once expired, joined or abandoned, a later verdict
    /// can't change it - the navigator and a chat answer never disagree about a meeting which just ended.
    /// </summary>
    [Test]
    public void EndsInOneTerminalState()
    {
        var meeting = new BotEventMeeting(new GameMapDefinition { Number = 3 }, null, DateTime.UtcNow.AddMinutes(5));

        Assert.That(meeting.IsActive, Is.True);
        Assert.That(meeting.AdvanceTo(BotMeetingState.Traveling), Is.True);
        Assert.That(meeting.AdvanceTo(BotMeetingState.Waiting), Is.True);
        Assert.That(meeting.AdvanceTo(BotMeetingState.Expired), Is.True);
        Assert.That(meeting.IsActive, Is.False);
        Assert.That(meeting.AdvanceTo(BotMeetingState.Abandoned), Is.False);
        Assert.That(meeting.State, Is.EqualTo(BotMeetingState.Expired));
    }

    /// <summary>
    /// A meeting whose time ran out ends as expired and is forgotten, so the bot returns to its routine
    /// instead of waiting at a place nobody can use anymore.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask ExpiresWhenTheEntranceClosedAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        var mapDefinition = CreateMap(4, CreateBlockedTerrainData());
        var map = new GameMap(mapDefinition, TimeSpan.FromMinutes(1), 8);
        bot.SetCurrentMapSilently(map);
        var meeting = new BotEventMeeting(mapDefinition, null, DateTime.UtcNow.AddMinutes(-1));
        bot.EventMeeting = meeting;

        var consumed = await new BotNavigator(bot).TryEventMeetingAsync(map, true, CancellationToken.None).ConfigureAwait(false);

        Assert.That(consumed, Is.False);
        Assert.That(bot.EventMeeting, Is.Null);
        Assert.That(meeting.State, Is.EqualTo(BotMeetingState.Expired));
        Assert.That(bot.LastEventMeeting, Is.Not.Null, "the outcome stays visible after the meeting is forgotten");
        Assert.That(bot.LastEventMeeting!.State, Is.EqualTo(nameof(BotMeetingState.Expired)));
        Assert.That(bot.LastEventMeeting.Place, Is.EqualTo("map 4 (safezone)"));
    }

    /// <summary>
    /// A meeting the bot can't get to is not just logged: the outcome names the state and the reason, so an
    /// operator can tell late that the promised meeting never happened.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask RecordsWhyAMeetingWasAbandonedAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        var currentMap = new GameMap(CreateMap(2, CreateBlockedTerrainData()), TimeSpan.FromMinutes(1), 8);
        bot.SetCurrentMapSilently(currentMap);
        bot.EventMeeting = new BotEventMeeting(CreateMap(5, CreateBlockedTerrainData()), null, DateTime.UtcNow.AddMinutes(5));

        var consumed = await new BotNavigator(bot).TryEventMeetingAsync(currentMap, false, CancellationToken.None).ConfigureAwait(false);

        Assert.That(consumed, Is.False);
        Assert.That(bot.EventMeeting, Is.Null);
        Assert.That(bot.LastEventMeeting, Is.Not.Null);
        Assert.That(bot.LastEventMeeting!.State, Is.EqualTo(nameof(BotMeetingState.Abandoned)));
        Assert.That(bot.LastEventMeeting.Place, Is.EqualTo("map 5 (safezone)"));
        Assert.That(bot.LastEventMeeting.Reason, Is.EqualTo("the meeting map has no gate to enter it"));
    }

    /// <summary>
    /// The entry NPC is cut off from the rest of the map: the bot doesn't give the meeting up, it waits
    /// in the safezone of the same map instead, where the players can still find it.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask WaitsInTheSafezoneWhenTheEntryNpcCantBeReachedAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);

        var terrain = CreateBlockedTerrainData();
        MarkWalkableArea(terrain, 10, 10, 10, 10);
        MarkWalkableArea(terrain, 200, 200, 201, 201);
        var mapDefinition = CreateMap(4, terrain);
        CreateGate(mapDefinition, 10, 10, 10, 10).IsSpawnGate = true;
        var map = new GameMap(mapDefinition, TimeSpan.FromMinutes(1), 8);
        bot.SetCurrentMapSilently(map);
        bot.Position = new Point(10, 10);
        bot.EventMeeting = new BotEventMeeting(mapDefinition, new Point(200, 200), DateTime.UtcNow.AddMinutes(5));
        var navigator = new BotNavigator(bot);

        var consumed = await navigator.TryEventMeetingAsync(map, false, CancellationToken.None).ConfigureAwait(false);

        Assert.That(consumed, Is.True);
        Assert.That(bot.EventMeeting, Is.Not.Null, "the meeting falls back instead of being given up");
        Assert.That(bot.EventMeeting!.NpcPosition, Is.Null, "the bot waits in the safezone of the map");
        Assert.That(bot.EventMeeting.Map, Is.SameAs(mapDefinition));
        Assert.That(bot.EventMeeting.State, Is.EqualTo(BotMeetingState.Traveling));

        // Reaching the safezone ends the travel: the bot waits there until the entrance closes.
        var waits = await navigator.TryEventMeetingAsync(map, true, CancellationToken.None).ConfigureAwait(false);

        Assert.That(waits, Is.True);
        Assert.That(bot.EventMeeting!.State, Is.EqualTo(BotMeetingState.Waiting));
    }

    /// <summary>
    /// Creates a terrain on which no tile is walkable and none is in the safezone - without data a
    /// terrain is the walkable default map, which would hide what the test sets up.
    /// </summary>
    private static GameMapTerrain CreateBlockedTerrain()
    {
        var terrain = new GameMapTerrain((byte[]?)null);
        Array.Clear(terrain.WalkMap);
        Array.Clear(terrain.SafezoneMap);
        return terrain;
    }

    private static byte[] CreateBlockedTerrainData()
    {
        var terrain = new byte[ushort.MaxValue + 3];
        Array.Fill(terrain, (byte)4, 3, ushort.MaxValue);
        return terrain;
    }

    private static void MarkWalkableArea(byte[] terrain, byte x1, byte y1, byte x2, byte y2)
    {
        for (var x = x1; x <= x2; x++)
        {
            for (var y = y1; y <= y2; y++)
            {
                terrain[3 + (y * 256) + x] = 0;
            }
        }
    }

    private static GameMapDefinition CreateMap(byte number, byte[] terrain)
        => new MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition
        {
            Id = Guid.NewGuid(),
            Number = number,
            TerrainData = terrain,
        };

    private static ExitGate CreateGate(GameMapDefinition map, byte x1, byte y1, byte x2, byte y2)
    {
        var gate = new MUnique.OpenMU.Persistence.BasicModel.ExitGate
        {
            Id = Guid.NewGuid(),
            Map = map,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
        };

        map.ExitGates.Add(gate);
        return gate;
    }

    private static void AddEntryNpc(GameMapDefinition map, byte x, byte y, NpcWindow window)
        => map.MonsterSpawns.Add(new MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea
        {
            Id = Guid.NewGuid(),
            GameMap = map,
            MonsterDefinition = new MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition
            {
                Id = Guid.NewGuid(),
                NpcWindow = window,
            },
            X1 = x,
            Y1 = y,
            X2 = x,
            Y2 = y,
            Quantity = 1,
        });
}
