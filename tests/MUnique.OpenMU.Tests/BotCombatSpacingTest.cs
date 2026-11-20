// <copyright file="BotCombatSpacingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests the spacing rules of <see cref="BotCombatSpacing"/>: when a ranged bot gives ground, where it
/// steps to and how a free-for-all event target is rated.
/// </summary>
[TestFixture]
public class BotCombatSpacingTest
{
    private IGameContext _gameContext = null!;

    /// <summary>Creates a fresh game context for every test.</summary>
    [SetUp]
    public void SetUp() => this._gameContext = GameContextTestHelper.CreateGameContext();

    /// <summary>
    /// A wizard which an opponent already reached steps back; a melee class in the same situation keeps
    /// fighting, because stepping back would only take it out of its own reach.
    /// </summary>
    [Test]
    public async ValueTask OnlyARangedClassGivesGroundAsync()
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        bot.Account!.IsBot = true;
        bot.Position = new Point(100, 100);
        var opponent = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        opponent.Position = new Point(102, 100);

        Assert.That(BotCombatSpacing.IsRangedClass(bot), Is.True, "the fixture class is a dark wizard");
        Assert.That(BotCombatSpacing.ShouldRetreat(bot, opponent, attackRange: 6, DateTime.UtcNow), Is.True);

        // Out of melee reach, out of range, or no ranged attack: no reason to step back.
        opponent.Position = new Point(110, 100);
        Assert.That(BotCombatSpacing.ShouldRetreat(bot, opponent, attackRange: 6, DateTime.UtcNow), Is.False);
        opponent.Position = new Point(102, 100);
        Assert.That(BotCombatSpacing.ShouldRetreat(bot, opponent, attackRange: 2, DateTime.UtcNow), Is.False);

        bot.SelectedCharacter!.CharacterClass!.Number = BotClassNumbers.DarkKnightNumber;
        Assert.That(BotCombatSpacing.IsRangedClass(bot), Is.False);
        Assert.That(BotCombatSpacing.ShouldRetreat(bot, opponent, attackRange: 6, DateTime.UtcNow), Is.False);
    }

    /// <summary>
    /// Only a real opponent makes a bot give ground: a monster is not worth it outside of a running
    /// invasion, where it is the boss the bot should not facetank.
    /// </summary>
    [Test]
    public async ValueTask DoesNotKiteWithoutAFairReasonAsync()
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        bot.Account!.IsBot = true;
        bot.Position = new Point(100, 100);
        var notAThreat = new Mock<IAttackable>();
        notAThreat.SetupGet(a => a.Position).Returns(new Point(101, 100));

        Assert.That(BotCombatSpacing.ShouldRetreat(bot, notAThreat.Object, 6, DateTime.UtcNow), Is.False);

        // A player-owned session never kites: optional AI must not change a human's offline hunting.
        bot.Account.IsBot = false;
        var opponent = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        opponent.Position = new Point(102, 100);
        Assert.That(BotCombatSpacing.ShouldRetreat(bot, opponent, 6, DateTime.UtcNow), Is.False);
    }

    /// <summary>
    /// The retreat point is the walkable tile straight away from the threat; a blocked straight escape
    /// falls back to a diagonal one, and a fully blocked map yields no point at all.
    /// </summary>
    [Test]
    public async ValueTask RetreatsToAWalkableTileAwayFromTheThreatAsync()
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        var map = bot.CurrentMap!;
        var from = new Point(100, 100);
        var threat = new Point(97, 100);

        // The open test terrain is walkable everywhere, so the first free tile straight away wins.
        var point = BotCombatSpacing.FindRetreatPoint(map, from, threat, 4);
        Assert.That(point, Is.Not.Null);
        Assert.That(point!.Value.X, Is.EqualTo(104));
        Assert.That(point.Value.Y, Is.EqualTo(100));

        var walkMap = map.Terrain.WalkMap;
        for (var x = 0; x < walkMap.GetLength(0); x++)
        {
            for (var y = 0; y < walkMap.GetLength(1); y++)
            {
                walkMap[x, y] = false;
            }
        }

        Assert.That(BotCombatSpacing.FindRetreatPoint(map, from, threat, 4), Is.Null, "every tile is blocked");

        // A wall straight ahead: the diagonal escape is used instead.
        walkMap[103, 103] = true;
        var diagonal = BotCombatSpacing.FindRetreatPoint(map, from, threat, 4);
        Assert.That(diagonal, Is.Not.Null);
        Assert.That(diagonal!.Value.X, Is.EqualTo(103));
        Assert.That(diagonal.Value.Y, Is.EqualTo(103));
    }

    /// <summary>
    /// Inside a free-for-all event a target with a broken shield is worth more than one with a full
    /// shield, and a low health bar breaks a tie of equally depleted shields.
    /// </summary>
    [Test]
    public async ValueTask RatesEventTargetsByShieldThenHealthAsync()
    {
        var fresh = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var brokenShield = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var brokenShieldLowHealth = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);

        AddValue(fresh, Stats.MaximumShield, 100);
        SetValue(fresh, Stats.CurrentShield, fresh.Attributes![Stats.MaximumShield]);
        AddValue(brokenShield, Stats.MaximumShield, 100);
        SetValue(brokenShield, Stats.CurrentShield, 0);
        SetValue(brokenShield, Stats.CurrentHealth, 100);
        AddValue(brokenShieldLowHealth, Stats.MaximumShield, 100);
        SetValue(brokenShieldLowHealth, Stats.CurrentShield, 0);
        SetValue(brokenShieldLowHealth, Stats.CurrentHealth, 10);

        var freshPriority = BotCombatSpacing.GetEventTargetPriority(fresh);
        var brokenPriority = BotCombatSpacing.GetEventTargetPriority(brokenShield);
        var brokenLowHealthPriority = BotCombatSpacing.GetEventTargetPriority(brokenShieldLowHealth);

        Assert.That(brokenPriority, Is.LessThan(freshPriority), "a broken shield is the better target");
        Assert.That(brokenLowHealthPriority, Is.LessThan(brokenPriority), "with the same shield, less health wins");

        // A player without an attribute system is not rateable and sorts last.
        var unrateable = new Mock<Player>(this._gameContext).Object;
        Assert.That(BotCombatSpacing.GetEventTargetPriority(unrateable), Is.EqualTo(int.MaxValue));
    }

    private static void AddValue(Player player, AttributeDefinition attribute, float value)
        => player.Attributes!.AddElement(new ConstValueAttribute(value, attribute), attribute);

    private static void SetValue(Player player, AttributeDefinition attribute, float value)
        => player.Attributes![attribute] = value;
}
