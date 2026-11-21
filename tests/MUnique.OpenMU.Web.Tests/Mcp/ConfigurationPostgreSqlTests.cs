// <copyright file="ConfigurationPostgreSqlTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>Checks MCP read-after-write using independent EF contexts rather than shared in-memory entities.</summary>
/// <remarks>Run alone with OPENMU_TEST_MCP_POSTGRES pointing to an empty disposable PostgreSQL database.</remarks>
[TestFixture]
[Explicit("Requires an empty disposable PostgreSQL database and a separate test process.")]
[NonParallelizable]
public sealed class ConfigurationPostgreSqlTests
{
    /// <summary>Applies are visible through generic and task reads while pending panel edits survive.</summary>
    [Test]
    public async Task AppliedConfigurationIsImmediatelyReadableAsync()
    {
        await PrepareDatabaseAsync().ConfigureAwait(false);
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        Guid id;
        using (var setup = provider.CreateNewContext())
        {
            var config = setup.CreateNew<GameConfiguration>();
            config.ExperienceRate = 3;
            id = config.GetId();
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }

        using IDataSource<GameConfiguration> source = new GameConfigurationDataSource(NullLogger<GameConfigurationDataSource>.Instance, provider);
        var original = await source.GetOwnerAsync().ConfigureAwait(false);
        var audit = new InMemoryAdminAuditRepository();
        var executor = AdminOperationTestServices.CreateExecutor(audit: audit);
        var confirmations = new AdminChangeConfirmation(TimeProvider.System);
        var settings = new AdminWriteSettings { ReadOnly = false };
        var plugins = new PluginWriteOperations(provider, source, new PlugInManager([], NullLoggerFactory.Instance, null, null), executor, confirmations, settings);
        var characters = new CharacterWriteOperations(provider, source, Mock.Of<ILoginServer>(), executor, confirmations, settings);
        var writes = new ConfigurationWriteOperations(provider, source, executor, confirmations, settings, audit, plugins, characters);
        var reads = new ConfigurationReadOperations(source, Mock.Of<IAdminReadModel>(), provider);
        var tasks = new ConfigurationTaskOperations(source, writes, executor, provider);
        var preview = await writes.UpdateAsync("GameConfiguration", id, "{\"ExperienceRate\":6}", false, null, default).ConfigureAwait(false);
        Assert.That(original.ExperienceRate, Is.EqualTo(3));
        await writes.UpdateAsync("GameConfiguration", id, "{\"ExperienceRate\":6}", true, preview["confirmationToken"]!.GetValue<string>(), default).ConfigureAwait(false);
        Assert.That((await reads.GetAsync("GameConfiguration", id, 0, default).ConfigureAwait(false))["item"]!["ExperienceRate"]!.GetValue<float>(), Is.EqualTo(6));
        Assert.That((await tasks.RatesGetAsync(default).ConfigureAwait(false))["experienceRate"]!.GetValue<float>(), Is.EqualTo(6));
        var panel = await source.GetOwnerAsync().ConfigureAwait(false);
        Assert.That(panel, Is.Not.SameAs(original));
        panel.MaximumLevel = 300;
        preview = await writes.UpdateAsync("GameConfiguration", id, "{\"ExperienceRate\":9}", false, null, default).ConfigureAwait(false);
        await writes.UpdateAsync("GameConfiguration", id, "{\"ExperienceRate\":9}", true, preview["confirmationToken"]!.GetValue<string>(), default).ConfigureAwait(false);
        Assert.That(await source.GetOwnerAsync().ConfigureAwait(false), Is.SameAs(panel));
        Assert.That(panel.MaximumLevel, Is.EqualTo(300));
        Assert.That((await source.GetContextAsync().ConfigureAwait(false)).HasChanges, Is.True);
        var query = await reads.QueryAsync("GameConfiguration", null, "eq", null, "ExperienceRate,MaximumLevel", 0, 10, default).ConfigureAwait(false);
        Assert.That(query["items"]![0]!["ExperienceRate"]!.GetValue<float>(), Is.EqualTo(9));
        Assert.That(query["items"]![0]!["MaximumLevel"]!.GetValue<int>(), Is.Not.EqualTo(300));
        Assert.That((await tasks.RatesGetAsync(default).ConfigureAwait(false))["experienceRate"]!.GetValue<float>(), Is.EqualTo(9));
        var create = await writes.CreateAsync("GameConfiguration", id, "Items", "{\"Width\":1,\"Height\":1}", false, null, default).ConfigureAwait(false);
        var created = await writes.CreateAsync("GameConfiguration", id, "Items", "{\"Width\":1,\"Height\":1}", true, create["confirmationToken"]!.GetValue<string>(), default).ConfigureAwait(false);
        var itemId = Guid.Parse(created["change"]!["id"]!.GetValue<string>());
        Assert.That((await reads.GetAsync("ItemDefinition", itemId, 0, default).ConfigureAwait(false))["item"]!["Width"]!.GetValue<int>(), Is.EqualTo(1));
        await (await source.GetContextAsync().ConfigureAwait(false)).SaveChangesAsync().ConfigureAwait(false);
        Assert.That((await source.GetOwnerAsync().ConfigureAwait(false)).ExperienceRate, Is.EqualTo(9));
        Assert.That((await source.GetOwnerAsync().ConfigureAwait(false)).MaximumLevel, Is.EqualTo(300));
        using var first = provider.CreateNewContext();
        using var second = provider.CreateNewContext();
        await ((IConfigurationEditContext)first).BeginConfigurationEditAsync(default).ConfigureAwait(false);
        await ((IConfigurationEditContext)second).BeginConfigurationEditAsync(default).ConfigureAwait(false);
        var a = (await first.GetByIdAsync<GameConfiguration>(id).ConfigureAwait(false))!;
        var b = (await second.GetByIdAsync<GameConfiguration>(id).ConfigureAwait(false))!;
        a.ExperienceRate = 12;
        b.ExperienceRate = 14;
        await first.SaveChangesAsync().ConfigureAwait(false);
        Assert.ThrowsAsync<PersistenceConflictException>(async () => await second.SaveChangesAsync().ConfigureAwait(false));
    }

    /// <summary>A configuration object which a persisted character references is not deleted, not even in preview.</summary>
    [Test]
    public async Task RefusesDeletingObjectsReferencedByPersistedDataAsync()
    {
        await PrepareDatabaseAsync().ConfigureAwait(false);
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        Guid configurationId;
        Guid mapId;
        Guid characterClassId;
        using (var setup = provider.CreateNewContext())
        {
            var config = setup.CreateNew<GameConfiguration>();
            var map = setup.CreateNew<GameMapDefinition>();
            map.Number = 7;
            config.Maps.Add(map);
            var homeMap = setup.CreateNew<GameMapDefinition>();
            homeMap.Number = 8;
            config.Maps.Add(homeMap);
            var characterClass = setup.CreateNew<CharacterClass>();
            characterClass.Number = 1;
            characterClass.Name = "Test Class";
            characterClass.HomeMap = homeMap;
            config.CharacterClasses.Add(characterClass);
            configurationId = config.GetId();
            mapId = map.GetId();
            characterClassId = characterClass.GetId();
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }

        using IDataSource<GameConfiguration> source = new GameConfigurationDataSource(NullLogger<GameConfigurationDataSource>.Instance, provider);
        var configuration = await source.GetOwnerAsync().ConfigureAwait(false);
        using (var player = provider.CreateNewPlayerContext(configuration))
        {
            var map = (await player.GetByIdAsync<GameMapDefinition>(mapId).ConfigureAwait(false))!;
            var account = player.CreateNew<Account>();
            account.LoginName = "referring";
            account.PasswordHash = "hash";
            var character = player.CreateNew<Character>();
            character.Name = "Referrer";
            character.CurrentMap = map;
            character.CharacterClass = await player.GetByIdAsync<CharacterClass>(characterClassId).ConfigureAwait(false);
            character.CreateDate = DateTime.UtcNow;
            character.KeyConfiguration = new byte[30];
            account.Characters.Add(character);
            await player.SaveChangesAsync().ConfigureAwait(false);
        }

        var audit = new InMemoryAdminAuditRepository();
        var confirmations = new AdminChangeConfirmation(TimeProvider.System);
        var settings = new AdminWriteSettings { ReadOnly = false };
        var executor = AdminOperationTestServices.CreateExecutor(audit: audit);
        var plugins = new PluginWriteOperations(provider, source, new PlugInManager([], NullLoggerFactory.Instance, null, null), executor, confirmations, settings);
        var characters = new CharacterWriteOperations(provider, source, Mock.Of<ILoginServer>(), executor, confirmations, settings);
        var writes = new ConfigurationWriteOperations(provider, source, executor, confirmations, settings, audit, plugins, characters);
        var exception = Assert.ThrowsAsync<AdminOperationException>(async () => await writes.DeleteAsync("GameConfiguration", configurationId, "Maps", mapId, false, null, default).ConfigureAwait(false));
        Assert.That(exception!.Message, Does.Contain("persisted player"));
        using var verify = provider.CreateNewContext();
        Assert.That(await verify.GetByIdAsync<GameMapDefinition>(mapId).ConfigureAwait(false), Is.Not.Null);
    }

    private static async ValueTask PrepareDatabaseAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OPENMU_TEST_MCP_POSTGRES");
        Assert.That(connection, Is.Not.Null.And.Not.Empty, "Set OPENMU_TEST_MCP_POSTGRES to an empty disposable database, with 'User Id=' and 'Password=' entries and trailing semicolons.");
        if (!ConnectionConfigurator.IsInitialized)
        {
            ConnectionConfigurator.Initialize(new DatabaseSettings(connection!));
        }

        await using var schema = new EntityDataContext();
        var creator = schema.GetService<IRelationalDatabaseCreator>();
        await schema.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await creator.CreateAsync().ConfigureAwait(false);
        await creator.CreateTablesAsync().ConfigureAwait(false);
    }

    private sealed class DatabaseSettings(string connection) : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => null;

        Task IDatabaseConnectionSettingProvider.InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        ConnectionSetting IDatabaseConnectionSettingProvider.GetConnectionSetting<T>() => new() { DatabaseEngine = DatabaseEngine.Npgsql, ConnectionString = connection };

        ConnectionSetting IDatabaseConnectionSettingProvider.GetConnectionSetting(Type contextType)
        {
            if (!typeof(DbContext).IsAssignableFrom(contextType))
            {
                throw new ArgumentException("Expected an EF context type.", nameof(contextType));
            }

            return new ConnectionSetting { DatabaseEngine = DatabaseEngine.Npgsql, ConnectionString = connection };
        }
    }
}
