// <copyright file="BotPartyFormationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.Offline;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Tests for <see cref="BotPartyComposer"/>: one buffer per party,
/// one of each build at most, buffers never left solo when placeable.
/// </summary>
[TestFixture]
public class BotPartyFormationTests
{
    private IGameContext _gameContext = null!;

    /// <summary>
    /// Sets up a fresh game context before each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._gameContext = GameContextTestHelper.CreateGameContext();
    }

    /// <summary>
    /// Tests that every buffer is placed with exactly one buffer
    /// and unique build keys per party.
    /// </summary>
    [Test]
    public async ValueTask BuffersLeadAndAllArePlacedAsync()
    {
        var buffers = new[]
        {
            await this.CreateBotAsync("A", 8, 40).ConfigureAwait(false),
            await this.CreateBotAsync("C", 8, 41).ConfigureAwait(false),
        };
        var others = new[]
        {
            await this.CreateBotAsync("B", 8, 45).ConfigureAwait(false),
            await this.CreateBotAsync("D", 8, 45).ConfigureAwait(false),
            await this.CreateBotAsync("F", 8, 46).ConfigureAwait(false),
            await this.CreateBotAsync("H", 4, 45).ConfigureAwait(false),
            await this.CreateBotAsync("J", 4, 46).ConfigureAwait(false),
            await this.CreateBotAsync("L", 4, 47).ConfigureAwait(false),
        };
        var candidates = buffers.Concat(others).OrderBy(BotResetHandler.GetEffectiveLevel).ToList();

        var formation = BotPartyComposer.Compose(candidates);

        Assert.That(formation.UnplacedBuffers, Is.Empty);
        Assert.That(formation.Parties, Has.Count.EqualTo(2));
        foreach (var party in formation.Parties)
        {
            Assert.That(party.Count(BotBuild.IsSupportElf), Is.EqualTo(1));
            Assert.That(party.Select(BotBuild.GetBuildKey).Distinct().Count(), Is.EqualTo(party.Count));
        }
    }

    /// <summary>
    /// Tests that no party forms without a buffer.
    /// </summary>
    [Test]
    public async ValueTask NoBufferMeansNoPartyAsync()
    {
        var candidates = new[]
        {
            await this.CreateBotAsync("H", 4, 50).ConfigureAwait(false),
            await this.CreateBotAsync("J", 4, 51).ConfigureAwait(false),
            await this.CreateBotAsync("L", 4, 52).ConfigureAwait(false),
        };

        for (var i = 0; i < 5; i++)
        {
            var formation = BotPartyComposer.Compose(candidates);

            Assert.That(formation.Parties, Is.Empty);
            Assert.That(formation.UnplacedBuffers, Is.Empty);
        }
    }

    /// <summary>
    /// Tests that duplicate build keys never share a party.
    /// </summary>
    [Test]
    public async ValueTask DuplicateBuildKeysNeverSharePartyAsync()
    {
        var archer1 = await this.CreateBotAsync("B", 8, 45).ConfigureAwait(false);
        var archer2 = await this.CreateBotAsync("D", 8, 45).ConfigureAwait(false);
        var candidates = new OfflinePlayer[]
        {
            await this.CreateBotAsync("A", 8, 40).ConfigureAwait(false),
            await this.CreateBotAsync("H", 4, 45).ConfigureAwait(false),
            archer1,
            archer2,
        };

        for (var i = 0; i < 10; i++)
        {
            var formation = BotPartyComposer.Compose(candidates);

            foreach (var party in formation.Parties)
            {
                Assert.That(party.Count(m => ReferenceEquals(m, archer1) || ReferenceEquals(m, archer2)), Is.LessThanOrEqualTo(1));
                Assert.That(party.Count(BotBuild.IsSupportElf), Is.EqualTo(1));
            }
        }
    }

    /// <summary>
    /// Tests that party span never exceeds the level gap.
    /// </summary>
    [Test]
    public async ValueTask PartySpanNeverExceedsLevelGapAsync()
    {
        var candidates = new[]
        {
            await this.CreateBotAsync("A", 8, 30).ConfigureAwait(false),
            await this.CreateBotAsync("C", 8, 31).ConfigureAwait(false),
            await this.CreateBotAsync("H", 4, 35).ConfigureAwait(false),
            await this.CreateBotAsync("J", 4, 42).ConfigureAwait(false),
            await this.CreateBotAsync("L", 12, 47).ConfigureAwait(false),
            await this.CreateBotAsync("N", 12, 54).ConfigureAwait(false),
            await this.CreateBotAsync("P", 0, 60).ConfigureAwait(false),
        }.OrderBy(BotResetHandler.GetEffectiveLevel).ToList();

        for (var i = 0; i < 10; i++)
        {
            var formation = BotPartyComposer.Compose(candidates);

            foreach (var party in formation.Parties)
            {
                var levels = party.Select(BotResetHandler.GetEffectiveLevel).ToList();
                Assert.That(levels.Max() - levels.Min(), Is.LessThanOrEqualTo(BotPartyPolicy.MaxLevelGap));
                Assert.That(party.Count(BotBuild.IsSupportElf), Is.EqualTo(1));
            }
        }
    }

    /// <summary>
    /// Tests that bots waiting for the same event become an event-run party, so one invitation takes
    /// the whole group in.
    /// </summary>
    [Test]
    public async ValueTask EventRunnersGroupByMeetingAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var eventDefinition = new MiniGameDefinition { Type = MiniGameType.BloodCastle, GameLevel = 1 };
        var meeting = new BotEventMeeting(map, null, now.AddMinutes(10), Event: eventDefinition);
        var bots = new[]
        {
            await this.CreateBotAsync("A", 8, 40).ConfigureAwait(false),
            await this.CreateBotAsync("H", 4, 41).ConfigureAwait(false),
            await this.CreateBotAsync("B", 8, 42).ConfigureAwait(false),
        };
        foreach (var bot in bots)
        {
            bot.EventMeeting = meeting;
            Assert.That(BotPartyReadiness.IsAvailable(bot, BotPartyGoal.EventRun, now), Is.True, $"available {bot.Name}");
            Assert.That(BotPartyReadiness.HasGearFor(bot, BotPartyGoal.EventRun), Is.True, $"gear {bot.Name}: {BotPartyReadiness.GetGearScore(bot)} for level {BotPartyReadiness.GetLevel(bot)}");
        }

        var formation = BotPartyComposer.Compose(
            bots.OrderBy(BotResetHandler.GetEffectiveLevel).ToList(),
            bot => BotPartyIntentPlanner.GetIntent(bot, now),
            now);

        Assert.That(formation.Plans, Has.Count.EqualTo(1));
        Assert.That(formation.Plans[0].Goal, Is.EqualTo(BotPartyGoal.EventRun));
        Assert.That(formation.Plans[0].Members, Has.Count.InRange(2, 3));
        Assert.That(formation.Plans[0].Intent.MiniGame, Is.SameAs(eventDefinition));
    }

    /// <summary>
    /// Tests that a boss attempt is led by a geared vanguard and not by a buffer.
    /// </summary>
    [Test]
    public async ValueTask BossAttemptIsLedByAVanguardAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var bots = new[]
        {
            await this.CreateBotAsync("A", 8, 50).ConfigureAwait(false),
            await this.CreateBotAsync("H", 4, 51).ConfigureAwait(false),
            await this.CreateBotAsync("B", 8, 52).ConfigureAwait(false),
        };
        foreach (var bot in bots)
        {
            bot.BossAttempt = new BotBossAttempt(map, now.AddMinutes(10));
        }

        var formation = BotPartyComposer.Compose(
            bots.OrderBy(BotResetHandler.GetEffectiveLevel).ToList(),
            bot => BotPartyIntentPlanner.GetIntent(bot, now),
            now);

        Assert.That(formation.Plans, Has.Count.EqualTo(1));
        Assert.That(formation.Plans[0].Goal, Is.EqualTo(BotPartyGoal.BossAttempt));
        Assert.That(BotPartyReadiness.GetRole(formation.Plans[0].Members[0]), Is.EqualTo(BotPartyRole.Vanguard));
    }

    /// <summary>
    /// Tests that a merchant on its shift gets an escort of guards.
    /// </summary>
    [Test]
    public async ValueTask MerchantGetsAnEscortAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var merchant = await this.CreateBotAsync("A", 4, 50).ConfigureAwait(false);
        merchant.MarketShift = new BotMarketShift(map, new Point(10, 10), "Store", now, now.AddMinutes(30));
        var guards = new[]
        {
            await this.CreateBotAsync("H", 4, 51).ConfigureAwait(false),
            await this.CreateBotAsync("B", 8, 52).ConfigureAwait(false),
        };

        var formation = BotPartyComposer.Compose(
            new[] { merchant }.Concat(guards).OrderBy(BotResetHandler.GetEffectiveLevel).ToList(),
            bot => BotPartyIntentPlanner.GetIntent(bot, now),
            now);

        Assert.That(formation.Plans, Has.Count.EqualTo(1));
        Assert.That(formation.Plans[0].Goal, Is.EqualTo(BotPartyGoal.MarketEscort));
        Assert.That(formation.Plans[0].Members[0], Is.SameAs(merchant));
        Assert.That(formation.Plans[0].Members, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// Tests that a boss-attempt leader picked for its readiness does not drag in a bot far below its
    /// level, which the next rebalance would only split up again.
    /// </summary>
    [Test]
    public async ValueTask BossAttemptKeepsLowLevelBotsOutAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var highKnight = await this.CreateBotAsync("H", 4, 90).ConfigureAwait(false);
        var highElf = await this.CreateBotAsync("B", 8, 90).ConfigureAwait(false);
        var lowKnight = await this.CreateBotAsync("C", 4, 40).ConfigureAwait(false);
        var bots = new[] { highKnight, highElf, lowKnight };
        foreach (var bot in bots)
        {
            bot.BossAttempt = new BotBossAttempt(map, now.AddMinutes(10));
        }

        var formation = BotPartyComposer.Compose(
            bots.OrderBy(BotResetHandler.GetEffectiveLevel).ToList(),
            bot => BotPartyIntentPlanner.GetIntent(bot, now),
            now);

        Assert.That(formation.Plans, Has.Count.EqualTo(1));
        Assert.That(formation.Plans[0].Goal, Is.EqualTo(BotPartyGoal.BossAttempt));
        Assert.That(formation.Plans[0].Members, Does.Not.Contain(lowKnight));
        var levels = formation.Plans[0].Members.Select(BotResetHandler.GetEffectiveLevel).ToList();
        Assert.That(levels.Max() - levels.Min(), Is.LessThanOrEqualTo(BotPartyPolicy.MaxLevelGap));
    }

    /// <summary>
    /// Tests that a boss attempt which has to fall back to a support leader still takes at most one
    /// support, so it is not a group of buffers without anyone to take the front.
    /// </summary>
    [Test]
    public async ValueTask BossAttemptHasAtMostOneSupportAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var leader = await this.CreateBotAsync("A", 8, 90).ConfigureAwait(false);
        var secondSupport = await this.CreateBotAsync("C", 8, 89).ConfigureAwait(false);
        var damage = await this.CreateBotAsync("B", 8, 80).ConfigureAwait(false);
        var bots = new[] { leader, secondSupport, damage };
        foreach (var bot in bots)
        {
            bot.BossAttempt = new BotBossAttempt(map, now.AddMinutes(10));
        }

        var formation = BotPartyComposer.Compose(
            bots.OrderBy(BotResetHandler.GetEffectiveLevel).ToList(),
            bot => BotPartyIntentPlanner.GetIntent(bot, now),
            now);

        Assert.That(formation.Plans, Has.Count.EqualTo(1));
        Assert.That(formation.Plans[0].Members.Count(BotBuild.IsSupportElf), Is.EqualTo(1));
        Assert.That(formation.Plans[0].Members, Does.Contain(damage));
    }

    /// <summary>
    /// Tests that a merchant which also has an event meeting still gets its escort instead of being
    /// left out of both the event party (it may not join one on a shift) and the escort.
    /// </summary>
    [Test]
    public async ValueTask MerchantWithAnEventMeetingStillGetsAnEscortAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var eventDefinition = new MiniGameDefinition { Type = MiniGameType.BloodCastle, GameLevel = 1 };
        var merchant = await this.CreateBotAsync("A", 4, 50).ConfigureAwait(false);
        merchant.MarketShift = new BotMarketShift(map, new Point(10, 10), "Store", now, now.AddMinutes(30));
        merchant.EventMeeting = new BotEventMeeting(map, null, now.AddMinutes(10), Event: eventDefinition);
        var guards = new[]
        {
            await this.CreateBotAsync("H", 4, 51).ConfigureAwait(false),
            await this.CreateBotAsync("B", 8, 52).ConfigureAwait(false),
        };

        var formation = BotPartyComposer.Compose(
            new[] { merchant }.Concat(guards).OrderBy(BotResetHandler.GetEffectiveLevel).ToList(),
            bot => BotPartyIntentPlanner.GetIntent(bot, now),
            now);

        Assert.That(formation.Plans, Has.Count.EqualTo(1));
        Assert.That(formation.Plans[0].Goal, Is.EqualTo(BotPartyGoal.MarketEscort));
        Assert.That(formation.Plans[0].Members[0], Is.SameAs(merchant));
    }

    /// <summary>
    /// Tests that the escort around a merchant does not count as a player taking it into a party, which
    /// would end the shift before the bot reached its stall.
    /// </summary>
    [Test]
    public async ValueTask MerchantEscortKeepsTheShiftGoingAsync()
    {
        var now = DateTime.UtcNow;
        var map = this._gameContext.Configuration.Maps.First();
        var merchant = await this.CreateBotAsync("A", 4, 50).ConfigureAwait(false);
        var guard = await this.CreateBotAsync("H", 4, 51).ConfigureAwait(false);
        var party = this._gameContext.PartyManager.CreateParty();
        await party.AddAsync(merchant).ConfigureAwait(false);
        await party.AddAsync(guard).ConfigureAwait(false);
        merchant.PartyGoal = BotPartyGoal.MarketEscort;
        guard.PartyGoal = BotPartyGoal.MarketEscort;
        var shift = new BotMarketShift(map, new Point(10, 10), "Store", now, now.AddMinutes(30));
        merchant.MarketShift = shift;
        var configuration = new BotMerchantConfiguration();

        Assert.That(
            BotMarketPlanner.GetEndReason(merchant, shift, configuration, now),
            Is.EqualTo(BotMarketPlanner.EndReason.None));

        merchant.PartyGoal = BotPartyGoal.Experience;
        Assert.That(
            BotMarketPlanner.GetEndReason(merchant, shift, configuration, now),
            Is.EqualTo(BotMarketPlanner.EndReason.JoinedParty));
    }

    /// <summary>
    /// Tests that a party is rebalanced when a member logged out or the group out-leveled the gap.
    /// </summary>
    [Test]
    public async ValueTask StalePartiesAreDetectedAsync()
    {
        var now = DateTime.UtcNow;
        var buffer = await this.CreateBotAsync("A", 8, 40).ConfigureAwait(false);
        var knight = await this.CreateBotAsync("H", 4, 60).ConfigureAwait(false);
        var party = this._gameContext.PartyManager.CreateParty();
        await party.AddAsync(buffer).ConfigureAwait(false);
        await party.AddAsync(knight).ConfigureAwait(false);
        buffer.PartyGoal = BotPartyGoal.Experience;
        knight.PartyGoal = BotPartyGoal.Experience;

        Assert.That(BotPartyComposer.IsStale(party, now), Is.True, "level gap");

        knight.Attributes![Stats.Level] = 45;
        Assert.That(BotPartyComposer.IsStale(party, now), Is.False, "valid experience party");

        await party.KickMySelfAsync(knight).ConfigureAwait(false);
        var single = this._gameContext.PartyManager.CreateParty();
        await single.AddAsync(buffer).ConfigureAwait(false);
        Assert.That(BotPartyComposer.IsStale(single, now), Is.True, "party too small");
    }

    private async ValueTask<OfflinePlayer> CreateBotAsync(string name, byte classNumber, int level)
    {
        var donor = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        var bot = new OfflinePlayer(this._gameContext) { Account = donor.Account };
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await bot.SetSelectedCharacterAsync(donor.SelectedCharacter!).ConfigureAwait(false);
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        donor.SelectedCharacter!.Name = name;
        donor.SelectedCharacter.CharacterClass = new CharacterClass { Number = classNumber };
        bot.Account!.IsBot = true;
        bot.IsAlive = true;
        bot.Attributes![Stats.Level] = level;
        bot.SelectedCharacter!.Inventory!.Items.Add(new Item { ItemSlot = 0, Definition = new ItemDefinition { DropLevel = (byte)Math.Min(level, byte.MaxValue) } });
        return bot;
    }
}
