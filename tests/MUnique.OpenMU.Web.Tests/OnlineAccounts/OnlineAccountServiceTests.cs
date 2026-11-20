// <copyright file="OnlineAccountServiceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.OnlineAccounts;

using System.Threading;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Web.Shared.Services;

/// <summary>
/// Tests for the online-accounts data services with local and distributed servers.
/// </summary>
[TestFixture]
public class OnlineAccountServiceTests
{
    /// <summary>
    /// Without in-process game servers no character can be resolved (distributed fallback),
    /// but accounts are still listed ordered by login name.
    /// </summary>
    [Test]
    public async Task LoggedInAccounts_WithoutInProcessServers_HaveNoCharacter()
    {
        var loginServer = new Mock<ILoginServer>();
        loginServer.Setup(s => s.GetSnapshotAsync()).Returns(
            new ValueTask<Dictionary<string, byte>>(new Dictionary<string, byte> { ["bUser"] = 1, ["aUser"] = 2 }));
        var service = new LoggedInAccountService(loginServer.Object, EmptyServerProvider(), new MemoryCache(new MemoryCacheOptions()), MUnique.OpenMU.Web.Tests.Mcp.AdminOperationTestServices.CreateExecutor());

        var result = await service.GetAsync(0, 20);

        Assert.That(result.Select(a => a.LoginName), Is.EqualTo(new[] { "aUser", "bUser" }));
        Assert.That(result.All(a => a.CharacterName is null), Is.True);
        Assert.That(result.All(a => a.GuildName is null), Is.True);
    }

    /// <summary>
    /// Pagination applies after ordering.
    /// </summary>
    [Test]
    public async Task LoggedInAccounts_PaginationAppliesAfterOrdering()
    {
        var loginServer = new Mock<ILoginServer>();
        loginServer.Setup(s => s.GetSnapshotAsync()).Returns(
            new ValueTask<Dictionary<string, byte>>(new Dictionary<string, byte> { ["cUser"] = 1, ["bUser"] = 1, ["aUser"] = 1 }));
        var service = new LoggedInAccountService(loginServer.Object, EmptyServerProvider(), new MemoryCache(new MemoryCacheOptions()), MUnique.OpenMU.Web.Tests.Mcp.AdminOperationTestServices.CreateExecutor());

        var result = await service.GetAsync(1, 1);

        Assert.That(result.Select(a => a.LoginName), Is.EqualTo(new[] { "bUser" }));
    }

    /// <summary>
    /// Without in-process game servers there are no offline sessions to list,
    /// and the plugin counts as unavailable so the tab stays hidden.
    /// </summary>
    [Test]
    public async Task OfflineAccounts_WithoutInProcessServers_AreEmpty()
    {
        var service = new OfflineAccountService(EmptyServerProvider());

        Assert.That(await service.GetAsync(0, 20), Is.Empty);
        Assert.That(service.IsOfflevelFeatureAvailable(), Is.False);
    }

    /// <summary>
    /// Without in-process game servers there are no bots to list
    /// and the bot tab stays hidden.
    /// </summary>
    [Test]
    public async Task BotAccounts_WithoutInProcessServers_AreEmpty()
    {
        var service = new BotAccountService(EmptyServerProvider(), new MemoryCache(new MemoryCacheOptions()));

        Assert.That(await service.GetAsync(0, 20), Is.Empty);
        Assert.That(service.IsBotFeatureAvailable(), Is.False);
    }

    /// <summary>A remote bot is visible in the table, but the local-only reset action is not offered.</summary>
    [Test]
    public async Task BotAccounts_ReadRemoteSnapshotAsync()
    {
        var server = new Mock<IManageableServer>();
        server.SetupGet(s => s.Id).Returns(300);
        server.As<IGameServerAdministration>().Setup(s => s.GetBotSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new LiveBotInfo("remote", "Hero", 42, 3, DateTime.UnixEpoch, null, null, 0, "Idle", "waiting", 800,
                "Idle", "waiting", 4, "failure", 2, DateTimeOffset.UtcNow.AddMinutes(2),
                new("Waiting", "Devias", null, DateTime.UtcNow.AddMinutes(3)), null,
                [new("Sword", "keep", "upgrade", null, DateTimeOffset.UtcNow)]),
        });
        var provider = new Mock<IServerProvider>();
        provider.SetupGet(p => p.Servers).Returns(new List<IManageableServer> { server.Object });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new BotAccountService(provider.Object, cache);

        Assert.That(service.IsBotFeatureAvailable(), Is.True);
        var bot = (await service.GetAsync(0, 20).ConfigureAwait(false)).Single();
        Assert.That(bot.ServerId, Is.EqualTo(300));
        Assert.That(bot.Meeting?.State, Is.EqualTo("Waiting"));
        Assert.That(bot.LootDecisions?.Single().Item, Is.EqualTo("Sword"));
        Assert.That(bot.Restarts, Is.EqualTo(2));
        Assert.That(bot.CanResetHealth, Is.False);
    }

    /// <summary>
    /// A non-game server with the same id must not hide the account's game server.
    /// Logging off the registry still precedes disconnecting, including when the account is already absent.
    /// </summary>
    /// <param name="serverId">The account's game server id.</param>
    /// <param name="prependNonGameServer">Whether a non-game server with that id precedes the game server.</param>
    /// <param name="disconnectResult">Whether the game server finds the account to disconnect.</param>
    [TestCase(0, true, true)]
    [TestCase(7, false, true)]
    [TestCase(7, false, false)]
    public async Task SetAccountOffline_DisconnectsMatchingGameServerAsync(byte serverId, bool prependNonGameServer, bool disconnectResult)
    {
        const string AccountName = "test-user";
        var calls = new List<string>();
        var loginServer = new Mock<ILoginServer>(MockBehavior.Strict);
        loginServer.Setup(s => s.LogOffAsync(AccountName, serverId))
            .Callback(() => calls.Add("logoff"))
            .Returns(ValueTask.CompletedTask);
        var gameServer = new Mock<IGameServer>(MockBehavior.Strict);
        gameServer.SetupGet(s => s.Id).Returns(serverId);
        gameServer.Setup(s => s.DisconnectAccountAsync(AccountName))
            .Callback(() => calls.Add("disconnect"))
            .Returns(new ValueTask<bool>(disconnectResult));
        var otherGameServer = new Mock<IGameServer>(MockBehavior.Strict);
        otherGameServer.SetupGet(s => s.Id).Returns(serverId + 1);
        var servers = new List<IManageableServer> { otherGameServer.Object, gameServer.Object };
        if (prependNonGameServer)
        {
            var nonGameServer = new Mock<IManageableServer>(MockBehavior.Strict);
            nonGameServer.SetupGet(s => s.Id).Returns(serverId);
            nonGameServer.SetupGet(s => s.Type).Returns(ServerType.Discord);
            servers.Insert(0, nonGameServer.Object);
        }

        var provider = new Mock<IServerProvider>();
        provider.SetupGet(p => p.Servers).Returns(servers);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new LoggedInAccountService(loginServer.Object, provider.Object, cache, MUnique.OpenMU.Web.Tests.Mcp.AdminOperationTestServices.CreateExecutor());

        await service.SetAccountOfflineAsync(new LoggedInAccount(AccountName, serverId)).ConfigureAwait(false);

        loginServer.Verify(s => s.LogOffAsync(AccountName, serverId), Times.Once);
        gameServer.Verify(s => s.DisconnectAccountAsync(AccountName), Times.Once);
        otherGameServer.Verify(s => s.DisconnectAccountAsync(It.IsAny<string>()), Times.Never);
        Assert.That(calls, Is.EqualTo(new[] { "logoff", "disconnect" }));
    }

    /// <summary>
    /// Stale login entries can still be logged off when no matching local game server exists.
    /// </summary>
    [Test]
    public async Task SetAccountOffline_WithoutMatchingGameServer_StillLogsOffAsync()
    {
        const string AccountName = "test-user";
        const byte ServerId = 0;
        var loginServer = new Mock<ILoginServer>(MockBehavior.Strict);
        loginServer.Setup(s => s.LogOffAsync(AccountName, ServerId)).Returns(ValueTask.CompletedTask);
        var nonGameServer = new Mock<IManageableServer>(MockBehavior.Strict);
        nonGameServer.SetupGet(s => s.Id).Returns(ServerId);
        nonGameServer.SetupGet(s => s.Type).Returns(ServerType.Discord);
        var otherGameServer = new Mock<IGameServer>(MockBehavior.Strict);
        otherGameServer.SetupGet(s => s.Id).Returns(ServerId + 1);
        var provider = new Mock<IServerProvider>();
        provider.SetupGet(p => p.Servers).Returns(new List<IManageableServer> { nonGameServer.Object, otherGameServer.Object });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new LoggedInAccountService(loginServer.Object, provider.Object, cache, MUnique.OpenMU.Web.Tests.Mcp.AdminOperationTestServices.CreateExecutor());

        await service.SetAccountOfflineAsync(new LoggedInAccount(AccountName, ServerId)).ConfigureAwait(false);

        loginServer.Verify(s => s.LogOffAsync(AccountName, ServerId), Times.Once);
        otherGameServer.Verify(s => s.DisconnectAccountAsync(It.IsAny<string>()), Times.Never);
    }

    private static IServerProvider EmptyServerProvider()
    {
        var provider = new Mock<IServerProvider>();
        provider.Setup(p => p.Servers).Returns(new List<IManageableServer>());
        return provider.Object;
    }
}
