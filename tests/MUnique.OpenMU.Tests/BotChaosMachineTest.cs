// <copyright file="BotChaosMachineTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Bots;
using NUnit.Framework;

/// <summary>
/// Tests for the planning of a chaos machine trip - which warp a bot takes to reach a goblin, and when it
/// rather stays where it is (see <see cref="BotChaosMachineHandler.PickWarp"/>).
/// </summary>
[TestFixture]
public class BotChaosMachineTest
{
    /// <summary>
    /// The price of a mix, as the bot has to budget for it. Ten million Zen dwarfs the fares below, so a
    /// warp which passes this check is affordable for the bot without losing its mix.
    /// </summary>
    private const long MixPrice = 10_000_000;

    /// <summary>
    /// The bot takes the cheapest warp to a map which has a goblin - not the first one it finds.
    /// </summary>
    [Test]
    public void CheapestWarpToAGoblinMapIsTaken()
    {
        var cheap = CreateWarp("Noria", hasChaosMachine: true, costs: 1_000, levelRequirement: 0);
        var expensive = CreateWarp("Atlans", hasChaosMachine: true, costs: 5_000, levelRequirement: 0);

        var picked = BotChaosMachineHandler.PickWarp([expensive, cheap], MixPrice + 10_000, MixPrice);

        Assert.That(picked, Is.SameAs(cheap));
    }

    /// <summary>
    /// A map without a goblin is no destination for this trip, however cheap the warp is.
    /// </summary>
    [Test]
    public void MapWithoutChaosMachineIsIgnored()
    {
        var withoutGoblin = CreateWarp("Lorencia", hasChaosMachine: false, costs: 100, levelRequirement: 0);
        var withGoblin = CreateWarp("Noria", hasChaosMachine: true, costs: 5_000, levelRequirement: 0);

        var picked = BotChaosMachineHandler.PickWarp([withoutGoblin, withGoblin], MixPrice + 10_000, MixPrice);

        Assert.That(picked, Is.SameAs(withGoblin));
    }

    /// <summary>
    /// A bot which would spend the Zen it needs for the mix on the fare stays home: arriving at the goblin
    /// with nothing to mix is worse than not going at all.
    /// </summary>
    [Test]
    public void WarpWhichEatsTheMixPriceIsRejected()
    {
        var warp = CreateWarp("Noria", hasChaosMachine: true, costs: 5_000, levelRequirement: 0);

        Assert.That(BotChaosMachineHandler.PickWarp([warp], MixPrice + 4_999, MixPrice), Is.Null);
        Assert.That(BotChaosMachineHandler.PickWarp([warp], MixPrice + 5_000, MixPrice), Is.SameAs(warp));
    }

    /// <summary>
    /// Without a warp at all - an empty warp list, as a configuration without warps has - there is nothing
    /// to pick.
    /// </summary>
    [Test]
    public void NoWarpListMeansNoTrip()
    {
        Assert.That(BotChaosMachineHandler.PickWarp([], MixPrice, MixPrice), Is.Null);
    }

    private static WarpInfo CreateWarp(string mapName, bool hasChaosMachine, int costs, int levelRequirement)
    {
        var map = new MUnique.OpenMU.Persistence.BasicModel.GameMapDefinition
        {
            Id = Guid.NewGuid(),
            Name = mapName,
            Number = (byte)Random.Shared.Next(1, 200),
        };

        if (hasChaosMachine)
        {
            var goblin = new MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition
            {
                Id = Guid.NewGuid(),
                NpcWindow = NpcWindow.ChaosMachine,
            };
            goblin.ItemCraftings.Add(new MUnique.OpenMU.Persistence.BasicModel.ItemCrafting { Id = Guid.NewGuid() });
            map.MonsterSpawns.Add(new MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea
            {
                Id = Guid.NewGuid(),
                MonsterDefinition = goblin,
                GameMap = map,
                X1 = 100,
                X2 = 100,
                Y1 = 100,
                Y2 = 100,
                Quantity = 1,
            });
        }

        var gate = new MUnique.OpenMU.Persistence.BasicModel.ExitGate
        {
            Id = Guid.NewGuid(),
            Map = map,
            IsSpawnGate = true,
        };

        return new MUnique.OpenMU.Persistence.BasicModel.WarpInfo
        {
            Id = Guid.NewGuid(),
            Name = mapName,
            Gate = gate,
            Costs = costs,
            LevelRequirement = levelRequirement,
        };
    }
}
