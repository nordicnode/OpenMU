// <copyright file="BotVariabilityTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using MonsterAttribute = MUnique.OpenMU.Persistence.BasicModel.MonsterAttribute;
using MonsterDefinition = MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition;

/// <summary>
/// Tests the human-like variability of the bot population: the persona mix, the deterministic persona
/// and seed derivation, and the per-session random source which makes a reported session replayable.
/// </summary>
[TestFixture]
public class BotVariabilityTest
{
    /// <summary>
    /// A mix is read as weighted persona entries, in the configured order.
    /// </summary>
    [Test]
    public void MixIsParsedWithWeights()
    {
        var parsed = BotPersonaProfile.ParseMix("balanced:7, aggressive:2,social:1");

        Assert.That(parsed.Select(x => x.Persona), Is.EqualTo(new[] { BotPersona.Balanced, BotPersona.Aggressive, BotPersona.Social }));
        Assert.That(parsed.Select(x => x.Weight), Is.EqualTo(new[] { 7, 2, 1 }));
    }

    /// <summary>
    /// A typo never leaves the bots without a persona: when nothing valid is left, the default mix applies.
    /// </summary>
    [Test]
    public void InvalidMixFallsBackToTheDefault()
    {
        Assert.That(BotPersonaProfile.ParseMix(string.Empty).Sum(x => x.Weight), Is.GreaterThan(0));
        Assert.That(BotPersonaProfile.ParseMix("agressiv:10,totaler:5").Select(x => x.Persona), Does.Contain(BotPersona.Balanced));
        Assert.That(BotPersonaProfile.ParseMix("balanced:-5").Sum(x => x.Weight), Is.GreaterThan(0));
        Assert.That(BotPersonaProfile.ParseMix(null).Sum(x => x.Weight), Is.GreaterThan(0));
    }

    /// <summary>
    /// Unknown entries are skipped while the valid ones of the same mix are kept - one bad name must not
    /// throw away the server's whole configuration.
    /// </summary>
    [Test]
    public void UnknownPersonaNamesAreIgnored()
    {
        var parsed = BotPersonaProfile.ParseMix("balanced:3,unknown:9,social:2");

        Assert.That(parsed.Select(x => x.Persona), Is.EqualTo(new[] { BotPersona.Balanced, BotPersona.Social }));
    }

    /// <summary>
    /// A character keeps its persona across restarts, so a behavior report always names the same style.
    /// </summary>
    [Test]
    public void PersonaIsStableForTheSameCharacter()
    {
        var first = BotPersonaProfile.PickPersona(BotPersonaProfile.DefaultMix, "bot42", "Epimetheus");
        var second = BotPersonaProfile.PickPersona(BotPersonaProfile.DefaultMix, "bot42", "Epimetheus");

        Assert.That(second, Is.EqualTo(first));
    }

    /// <summary>
    /// The default mix spreads the population over every persona, and the dominant balanced style stays
    /// the clear majority - personas are a variation of the norm, not a new norm.
    /// </summary>
    [Test]
    public void DefaultMixSpreadsThePopulation()
    {
        var counts = Enumerable.Range(0, 2000)
            .Select(i => BotPersonaProfile.PickPersona(BotPersonaProfile.DefaultMix, $"bot{i}", $"Bot{i}"))
            .GroupBy(persona => persona)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.That(counts.Keys, Is.EquivalentTo(Enum.GetValues<BotPersona>()));
        Assert.That(counts[BotPersona.Balanced], Is.GreaterThan(1000));
        Assert.That(counts[BotPersona.Aggressive], Is.InRange(100, 300));
        Assert.That(counts[BotPersona.Cautious], Is.InRange(100, 300));
    }

    /// <summary>
    /// The session seed is stable for a character, differs between characters and never repeats the
    /// sequence of the shared source, and a server salt changes it on purpose.
    /// </summary>
    [Test]
    public void SeedIsDerivedFromTheIdentity()
    {
        var seed = BotPersonaProfile.DeriveSeed("bot1", "Alpha", 0);

        Assert.That(seed, Is.Not.Zero);
        Assert.That(BotPersonaProfile.DeriveSeed("bot1", "Alpha", 0), Is.EqualTo(seed));
        Assert.That(BotPersonaProfile.DeriveSeed("bot1", "Beta", 0), Is.Not.EqualTo(seed));
        Assert.That(BotPersonaProfile.DeriveSeed("bot2", "Alpha", 0), Is.Not.EqualTo(seed));
        Assert.That(BotPersonaProfile.DeriveSeed("bot1", "Alpha", 7), Is.Not.EqualTo(seed));
    }

    /// <summary>
    /// The guardrail of the whole feature: the same seed produces the same sequence, so a logged session
    /// can be replayed step by step.
    /// </summary>
    [Test]
    public void SameSeedReplaysTheSameSequence()
    {
        var first = new BotSessionRandom(0x1234_5678_9ABC_DEF0);
        var second = new BotSessionRandom(0x1234_5678_9ABC_DEF0);

        var firstValues = Enumerable.Range(0, 20).Select(_ => first.NextDouble()).ToList();
        var secondValues = Enumerable.Range(0, 20).Select(_ => second.NextDouble()).ToList();

        Assert.That(secondValues, Is.EqualTo(firstValues));
        Assert.That(new BotSessionRandom(0x1234_5678_9ABC_DEF1).NextDouble(), Is.Not.EqualTo(firstValues[0]));
    }

    /// <summary>
    /// Every helper stays inside its bounds, including the degenerate ranges a caller may pass.
    /// </summary>
    [Test]
    public void DrawsStayInsideTheirBounds()
    {
        var random = new BotSessionRandom(99);

        for (var i = 0; i < 200; i++)
        {
            Assert.That(random.NextInt(3, 9), Is.InRange(3, 8));
            Assert.That(random.NextUInt(10, 20), Is.InRange(10u, 19u));
            Assert.That(random.NextDouble(), Is.InRange(0.0, 1.0));
            Assert.That(random.NextTimeSpan(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)).TotalSeconds, Is.InRange(2.0, 5.0));
        }

        Assert.That(random.NextInt(5, 5), Is.EqualTo(5));
        Assert.That(random.NextInt(5, 1), Is.EqualTo(5));
        Assert.That(random.NextInt(0, 0), Is.Zero);
    }

    /// <summary>
    /// The personas shift their dials in the intended direction, and inside bounds which cannot make a
    /// bot reckless: the aggressive profile still keeps most of the cautious one's headroom.
    /// </summary>
    [Test]
    public void PersonasShiftBoundedDials()
    {
        var aggressive = BotPersonaProfile.Of(BotPersona.Aggressive);
        var balanced = BotPersonaProfile.Of(BotPersona.Balanced);
        var cautious = BotPersonaProfile.Of(BotPersona.Cautious);
        var trader = BotPersonaProfile.Of(BotPersona.Trader);
        var social = BotPersonaProfile.Of(BotPersona.Social);

        Assert.That(aggressive.CombatTolerance, Is.GreaterThan(balanced.CombatTolerance));
        Assert.That(cautious.CombatTolerance, Is.LessThan(balanced.CombatTolerance));
        Assert.That(aggressive.CombatTolerance, Is.LessThanOrEqualTo(1.3f));
        Assert.That(cautious.CombatTolerance, Is.GreaterThanOrEqualTo(0.8f));
        Assert.That(trader.ShoppingSlotBonus, Is.GreaterThan(0));
        Assert.That(social.SocialDelayFactor, Is.LessThan(balanced.SocialDelayFactor));
        Assert.That(cautious.SocialDelayFactor, Is.GreaterThan(balanced.SocialDelayFactor));
    }

    /// <summary>
    /// The profile of a bot follows its assigned persona; a session without one falls back to the
    /// balanced profile, so the feature never changes a human's offline session.
    /// </summary>
    [Test]
    public async ValueTask ProfileOfAPlayerFollowsItsPersonaAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var player = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);

        Assert.That(BotPersonaProfile.For(player), Is.SameAs(BotPersonaProfile.Of(BotPersona.Balanced)));

        player.Persona = BotPersona.Cautious;
        Assert.That(BotPersonaProfile.For(player), Is.SameAs(BotPersonaProfile.Of(BotPersona.Cautious)));
    }

    /// <summary>
    /// The dial means what its name says: a hit which is too big for a cautious bot is still acceptable to
    /// an aggressive one. Deliberately measured through <see cref="Offline.CombatHandler.IsSafeTarget"/>
    /// rather than on the profile numbers, because a tolerance factor applied to a maximum is easy to get
    /// backwards - and did ship backwards once.
    /// </summary>
    [Test]
    public async ValueTask AggressiveAcceptsAHitWhichCautiousRefusesAsync()
    {
        var gameContext = GameContextTestHelper.CreateGameContext();
        var bot = await PlayerTestHelper.CreateOfflineLevelingPlayerAsync(gameContext).ConfigureAwait(false);
        await bot.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        bot.IsAlive = true;
        bot.Attributes![Stats.Level] = 100;

        // The test character class brings neither a damage stat nor a damage relation, so the attack power
        // has to be added to the live attribute system. Without it IsSafeTarget refuses every fight for the
        // attack check alone, and the damage-per-hit decision would never be reached.
        Assert.That(bot.Attributes.TryAddStatAttribute(new StatAttribute(Stats.MinimumPhysBaseDmg, 1000)), Is.True);
        Assert.That(bot.Attributes.TryAddStatAttribute(new StatAttribute(Stats.MaximumPhysBaseDmg, 1000)), Is.True);

        // 17% of the bot's health per hit: above the balanced threshold (15%), and above what the cautious
        // profile allows (12.75%), but below what the aggressive one accepts (18.75%). The values are read
        // back, because the attribute system composes the maximum health of the character with its class
        // and level - writing a base value does not necessarily define the final one.
        var health = bot.Attributes[Stats.MaximumHealth];
        Assert.That(health, Is.GreaterThan(0f));
        var monster = CreateBruiserDefinition(bot.Attributes[Stats.DefensePvm] + (health * 0.17f));

        bot.Persona = BotPersona.Cautious;
        Assert.That(CombatHandler.IsSafeTarget(bot, monster), Is.False, "the cautious profile must keep its distance");

        bot.Persona = BotPersona.Balanced;
        Assert.That(CombatHandler.IsSafeTarget(bot, monster), Is.False, "the balanced profile keeps the original threshold");

        bot.Persona = BotPersona.Aggressive;
        Assert.That(CombatHandler.IsSafeTarget(bot, monster), Is.True, "the aggressive profile accepts the bigger hit");
    }

    /// <summary>
    /// The monster of <see cref="AggressiveAcceptsAHitWhichCautiousRefusesAsync"/>: it hits hard, but dies in
    /// one hit of the bot, so only the damage-per-hit check decides whether the fight is safe.
    /// </summary>
    /// <param name="damage">The average damage of one hit.</param>
    /// <returns>The monster definition.</returns>
    private static MonsterDefinition CreateBruiserDefinition(float damage)
    {
        var definition = new MonsterDefinition
        {
            Id = Guid.NewGuid(),
            Number = 32123,
            ObjectKind = NpcObjectKind.Monster,
        };
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.Level, Value = 10 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MinimumPhysBaseDmg, Value = damage });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumPhysBaseDmg, Value = damage });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.DefenseBase, Value = 0 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 1 });
        definition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.AttackRatePvm, Value = 0 });
        return definition;
    }
}
