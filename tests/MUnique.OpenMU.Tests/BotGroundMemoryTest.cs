// <copyright file="BotGroundMemoryTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;
using NUnit.Framework;

/// <summary>
/// Tests the short-term memory of the bad spots a bot hunts on: what it remembers, for how long, how the
/// memory fades with distance, and that it never blocks a ground completely.
/// </summary>
[TestFixture]
public class BotGroundMemoryTest
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// A ground the bot got wedged on loses its weight and counts as avoided while it is remembered.
    /// </summary>
    [Test]
    public void RemembersWhereTheBotGotWedged()
    {
        var memory = new BotGroundMemory(this._time);
        var map = new GameMapDefinition { Number = 3 };
        var spot = new Point(100, 100);

        memory.Remember(map, spot, GroundMemoryReason.Stuck);
        memory.Update();

        Assert.That(memory.GetWeightFactor(map, spot), Is.LessThan(0.5f));
        Assert.That(memory.IsAvoided(map, spot), Is.True);
    }

    /// <summary>
    /// The memory fades with distance: a ground a few tiles away is discounted less than the spot itself,
    /// and one outside the range is not affected at all.
    /// </summary>
    [Test]
    public void MemoryFadesWithDistance()
    {
        var memory = new BotGroundMemory(this._time);
        var map = new GameMapDefinition { Number = 3 };

        memory.Remember(map, new Point(100, 100), GroundMemoryReason.Died);
        memory.Update();

        var atSpot = memory.GetWeightFactor(map, new Point(100, 100));
        var nearby = memory.GetWeightFactor(map, new Point(100, 108));
        var farAway = memory.GetWeightFactor(map, new Point(100, 170));

        Assert.That(atSpot, Is.LessThan(nearby));
        Assert.That(nearby, Is.LessThan(1f));
        Assert.That(farAway, Is.EqualTo(1f));
    }

    /// <summary>
    /// A wedge is forgotten after a few minutes, a death is remembered longer - dying is the stronger
    /// signal, and a stale wedge must not keep a bot off good ground forever.
    /// </summary>
    [Test]
    public void ForgetsAfterItsLifetime()
    {
        var memory = new BotGroundMemory(this._time);
        var map = new GameMapDefinition { Number = 3 };
        var spot = new Point(100, 100);

        memory.Remember(map, spot, GroundMemoryReason.Stuck);
        memory.Remember(map, spot, GroundMemoryReason.Died);
        memory.Update();

        this._time.Advance(BotGroundMemory.StuckLifetime + TimeSpan.FromSeconds(1));
        memory.Update();
        Assert.That(memory.GetWeightFactor(map, spot), Is.LessThan(1f), "a death is remembered longer than a wedge");

        this._time.Advance(BotGroundMemory.DeathLifetime);
        memory.Update();
        Assert.That(memory.IsAvoided(map, spot), Is.False);
        Assert.That(memory.GetWeightFactor(map, spot), Is.EqualTo(1f));
    }

    /// <summary>
    /// The memory belongs to one map: the same coordinates elsewhere stay fully attractive.
    /// </summary>
    [Test]
    public void MemoryIsPerMap()
    {
        var memory = new BotGroundMemory(this._time);
        var rememberedMap = new GameMapDefinition { Number = 3 };
        var otherMap = new GameMapDefinition { Number = 4 };
        var spot = new Point(100, 100);

        memory.Remember(rememberedMap, spot, GroundMemoryReason.Died);
        memory.Update();

        Assert.That(memory.IsAvoided(rememberedMap, spot), Is.True);
        Assert.That(memory.IsAvoided(otherMap, spot), Is.False);
        Assert.That(memory.GetWeightFactor(otherMap, spot), Is.EqualTo(1f));
    }

    /// <summary>
    /// The entries are capped: a bot which keeps failing does not grow an unbounded list, and the memory
    /// of a failure never drops to zero - the ground stays a bad, but possible, pick.
    /// </summary>
    [Test]
    public void MemoryIsBoundedAndNeverBlocks()
    {
        var memory = new BotGroundMemory(this._time);
        var map = new GameMapDefinition { Number = 3 };

        for (var i = 0; i < 100; i++)
        {
            memory.Remember(map, new Point((byte)(i % 200), (byte)(i % 150)), GroundMemoryReason.Stuck);
        }

        memory.Update();

        var factor = memory.GetWeightFactor(map, new Point(0, 0));
        Assert.That(factor, Is.GreaterThan(0f));
    }

    /// <summary>
    /// A simple clock the tests move by hand.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => this._utcNow;

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        /// <param name="timeSpan">The time to advance.</param>
        public void Advance(TimeSpan timeSpan) => this._utcNow += timeSpan;
    }
}
