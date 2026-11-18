// <copyright file="BotPlayerFixture.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Creates bots which are actually in the world, so the paths which need a running navigation can be
/// exercised: a bot reports what it does, and the administration surfaces read that report.
/// </summary>
internal static class BotPlayerFixture
{
    /// <summary>The login name the fixture bot drives, unless a test names its own.</summary>
    internal const string DefaultLoginName = "fixture-bot";

    /// <summary>The character name the fixture bot drives, unless a test names its own.</summary>
    internal const string DefaultCharacterName = "FixtureBot";

    /// <summary>
    /// Persists an account with one character and starts a bot driving it, which is the same way the bot
    /// feature spawns a bot - only with a bounded, in-memory world.
    /// </summary>
    /// <param name="gameContext">The game context, e.g. from <see cref="GameContextTestHelper.CreateGameContext"/>.</param>
    /// <param name="loginName">The login name of the bot account.</param>
    /// <param name="characterName">The character of that account the bot drives.</param>
    /// <returns>The started bot; the caller disposes it.</returns>
    internal static async ValueTask<BotPlayer> CreateBotAsync(
        IGameContext gameContext,
        string loginName = DefaultLoginName,
        string characterName = DefaultCharacterName)
    {
        var characterClass = AddCharacterClass(gameContext);
        await PersistAccountAsync(gameContext, loginName, characterName, characterClass).ConfigureAwait(false);

        var bot = new BotPlayer(gameContext) { MuHelperSettings = new BotMuHelperSettings() };
        try
        {
            var started = await bot.InitializeAsync(loginName, characterName).ConfigureAwait(false);
            if (!started)
            {
                throw new InvalidOperationException($"The fixture bot could not start for account '{loginName}'.");
            }

            return bot;
        }
        catch
        {
            await bot.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static MUnique.OpenMU.DataModel.Configuration.CharacterClass AddCharacterClass(IGameContext gameContext)
    {
        if (gameContext.Configuration.CharacterClasses.FirstOrDefault() is { } existing)
        {
            return existing;
        }

        using var context = gameContext.PersistenceContextProvider.CreateNewConfigurationContext();
        var characterClass = context.CreateNew<CharacterClass>();
        characterClass.Number = 0;
        characterClass.Name = "Fixture Dark Knight";
        foreach (var (attribute, baseValue) in new (AttributeSystem.AttributeDefinition Attribute, float BaseValue)[]
        {
            (Stats.Level, 10),
            (Stats.MasterLevel, 0),
            (Stats.BaseStrength, 28),
            (Stats.BaseAgility, 20),
            (Stats.BaseVitality, 25),
            (Stats.BaseEnergy, 10),
            (Stats.CurrentHealth, 0),
            (Stats.CurrentMana, 0),
            (Stats.CurrentShield, 0),
        })
        {
            characterClass.StatAttributes.Add(context.CreateNew<StatAttributeDefinition>(attribute, baseValue, false));
        }

        gameContext.Configuration.CharacterClasses.Add(characterClass);
        return characterClass;
    }

    private static async ValueTask PersistAccountAsync(IGameContext gameContext, string loginName, string characterName, MUnique.OpenMU.DataModel.Configuration.CharacterClass characterClass)
    {
        using var context = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration);
        var account = context.CreateNew<Account>();
        account.LoginName = loginName;
        account.PasswordHash = "not-a-real-hash";

        var character = context.CreateNew<Character>();
        character.Name = characterName;
        character.CharacterClass = characterClass;
        character.CurrentMap = gameContext.Configuration.Maps.First();
        character.PositionX = 130;
        character.PositionY = 130;
        account.Characters.Add(character);

        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}
