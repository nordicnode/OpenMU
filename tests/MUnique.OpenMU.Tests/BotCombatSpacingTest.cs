// <copyright file="BotCombatSpacingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
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
    /// falls back to a diagonal one, and a fully blocked map yields no point at all. A free tile which
    /// can only be reached around a wall is not a retreat point either.
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

        // A free tile behind a wall is no escape: the path around the obstacle would lead the bot back
        // past the threat it is trying to get away from.
        walkMap[104, 100] = true;
        Assert.That(BotCombatSpacing.FindRetreatPoint(map, from, threat, 4), Is.Null, "the straight way is walled off");

        // A clear diagonal lane is used instead of the blocked straight one.
        walkMap[101, 101] = true;
        walkMap[102, 102] = true;
        walkMap[103, 103] = true;
        var diagonal = BotCombatSpacing.FindRetreatPoint(map, from, threat, 4);
        Assert.That(diagonal, Is.Not.Null);
        Assert.That(diagonal!.Value.X, Is.EqualTo(103));
        Assert.That(diagonal.Value.Y, Is.EqualTo(103));
    }

    /// <summary>
    /// Every retreat candidate is at most the requested distance away - also the perpendicular escapes
    /// of a diagonal threat, which are longer than one tile before they are scaled back.
    /// </summary>
    [Test]
    public async ValueTask KeepsTheRetreatDistanceOfThePerpendicularEscapeAsync()
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        var map = bot.CurrentMap!;
        var from = new Point(100, 100);
        var threat = new Point(97, 97);

        // A wall on the straight diagonal escape leaves the perpendicular ones, which are two tiles long
        // per tile of the direct direction: unscaled they would jump twice the requested distance.
        var walkMap = map.Terrain.WalkMap;
        for (var offset = 1; offset <= 4; offset++)
        {
            walkMap[from.X + offset, from.Y + offset] = false;
        }

        var point = BotCombatSpacing.FindRetreatPoint(map, from, threat, 4);

        Assert.That(point, Is.Not.Null);
        var distance = Math.Max(Math.Abs(point!.Value.X - from.X), Math.Abs(point.Value.Y - from.Y));
        Assert.That(distance, Is.EqualTo(4), "the escape is not twice as far as the requested distance");
    }

    /// <summary>
    /// A walking bot needs a walkable way to its retreat point, but a teleport does not: it may blink
    /// over the wall or pillar which blocks the direct escape.
    /// </summary>
    [Test]
    public async ValueTask BlindsOverAnObstacleWhenTheWayIsBlockedAsync()
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        var map = bot.CurrentMap!;
        var from = new Point(100, 100);
        var threat = new Point(97, 100);

        // A wall right behind the bot; the tile behind it is free.
        var walkMap = map.Terrain.WalkMap;
        for (var x = 101; x <= 103; x++)
        {
            walkMap[x, 100] = false;
        }

        var walking = BotCombatSpacing.FindRetreatPoint(map, from, threat, 4, requiresWalkablePath: true);
        Assert.That(walking, Is.Not.Null);
        Assert.That(walking!.Value, Is.Not.EqualTo(new Point(104, 100)), "the wall blocks the direct escape");

        var teleporting = BotCombatSpacing.FindRetreatPoint(map, from, threat, 4, requiresWalkablePath: false);
        Assert.That(teleporting, Is.Not.Null);
        Assert.That(teleporting!.Value, Is.EqualTo(new Point(104, 100)), "the blink lands behind the wall");
    }

    /// <summary>
    /// The teleport retreat follows the learned skill, not a hard-coded class number: the old
    /// configurations teach it to the dark wizard itself. A teleport the bot cannot pay for is no
    /// escape, because the game refuses it and the bot would stand still in melee reach.
    /// </summary>
    [Test]
    public async ValueTask OnlyTeleportsWhenTheSkillIsKnownAndAffordableAsync()
    {
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(this._gameContext).ConfigureAwait(false);
        bot.SelectedCharacter!.CharacterClass!.Number = BotClassNumbers.DarkWizardNumber;
        bot.Attributes![Stats.CurrentMana] = 0;

        Assert.That(BotCombatSpacing.IsRangedClass(bot), Is.True, "a dark wizard fights from a distance");
        Assert.That(BotCombatSpacing.CanTeleportAway(bot), Is.False, "without the skill there is nothing to blink with");

        var teleport = new TestSkill { Number = (short)BotCombatSpacing.TeleportSkillNumber };
        teleport.ConsumeRequirements.Add(new AttributeRequirement { Attribute = Stats.CurrentMana, MinimumValue = 30 });
        await bot.SkillList!.AddLearnedSkillAsync(teleport).ConfigureAwait(false);

        Assert.That(BotCombatSpacing.CanTeleportAway(bot), Is.False, "no mana, no blink - the bot walks instead");

        bot.Attributes[Stats.CurrentMana] = 100;

        Assert.That(BotCombatSpacing.CanTeleportAway(bot), Is.True, "a dark wizard who learned teleport and can pay for it may use it");
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

    private sealed class TestSkill : Skill
    {
        public TestSkill()
        {
            this.Requirements = new List<AttributeRequirement>();
            this.ConsumeRequirements = new List<AttributeRequirement>();
            this.AttributeRelationships = new List<AttributeRelationship>();
            this.Range = 6;
            this.SkillType = SkillType.Other;
            this.DamageType = DamageType.Wizardry;
        }
    }
}
