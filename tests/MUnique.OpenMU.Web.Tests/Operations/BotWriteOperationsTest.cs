// <copyright file="BotWriteOperationsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Operations;

using System.Threading;
using Moq;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Web.AdminPanel.Operations;
using MUnique.OpenMU.Web.Tests.Mcp;
using NUnit.Framework;

/// <summary>
/// Tests the operator's lever on the bot health record: forgetting what the server counts against an account.
/// </summary>
[TestFixture]
public class BotWriteOperationsTest
{
    /// <summary>
    /// The reset is refused without a name, because it would silently clear every bot of every account otherwise.
    /// </summary>
    [Test]
    public void RefusesToResetWithoutAnAccount()
    {
        var operation = CreateOperation();

        var exception = Assert.ThrowsAsync<AdminOperationException>(async () => await operation.ResetHealthAsync(" ", CancellationToken.None).ConfigureAwait(false));

        Assert.That(exception!.Kind, Is.EqualTo(AdminOperationErrorKind.InvalidInput));
    }

    /// <summary>
    /// An account which runs no bot is a mistyped name, not a silent success - the caller gets the actual reason.
    /// </summary>
    [Test]
    public void ReportsAnAccountWithoutBots()
    {
        var operation = CreateOperation();

        var exception = Assert.ThrowsAsync<AdminResourceNotFoundException>(async () => await operation.ResetHealthAsync("nobody", CancellationToken.None).ConfigureAwait(false));

        Assert.That(exception!.Message, Does.Contain("bots_list"));
    }

    /// <summary>
    /// The answer names the bots it acted on, per server, instead of only claiming success - including what was
    /// quarantined before the reset.
    /// </summary>
    [Test]
    public async Task ResetsTheBotsOfTheAccountItWasGivenAsync()
    {
        var server = Server(300, [new LiveBotHealthReset("Flapper", 1, 2, 1, 300)]);
        var operation = CreateOperation(server.Object);

        var result = await operation.ResetHealthAsync("flapper", CancellationToken.None).ConfigureAwait(false);

        Assert.That(result["bots"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(result["summary"]!.GetValue<string>(), Does.Contain("forgotten"));
        Assert.That(result["bots"]!.AsArray()[0]!["quarantinedSeconds"]!.GetValue<int>(), Is.EqualTo(300));
        Assert.That(result["bots"]!.AsArray()[0]!["serverId"]!.GetValue<int>(), Is.EqualTo(300));
        Assert.That(result["serversNotChecked"]!.AsArray(), Is.Empty);
    }

    /// <summary>
    /// A server which is not hosted in this process is reset through its administration channel, not skipped -
    /// a remote server behind Dapr answers the same request.
    /// </summary>
    [Test]
    public async Task ResetsRemoteServersThroughTheirAdministrationChannelAsync()
    {
        var local = Server(300, [new LiveBotHealthReset("Flapper", 1, 0, 0, 0)]);
        var remote = Server(301, [new LiveBotHealthReset("Flapper", 1, 0, 0, 0)]);
        var operation = CreateOperation(local.Object, remote.Object);

        var result = await operation.ResetHealthAsync("flapper", CancellationToken.None).ConfigureAwait(false);

        remote.Verify(s => s.ResetBotHealthAsync("flapper", It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(result["bots"]!.AsArray().Select(bot => bot!["serverId"]!.GetValue<int>()), Is.EquivalentTo(new[] { 300, 301 }));
    }

    /// <summary>A server which cannot be reached is reported instead of being silently skipped.</summary>
    [Test]
    public async Task ReportsServersItCouldNotCheckAsync()
    {
        var local = Server(300, [new LiveBotHealthReset("Flapper", 1, 0, 0, 0)]);
        var unreachable = Server(301, []);
        unreachable.Setup(s => s.ResetBotHealthAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("dapr sidecar unavailable"));
        var plain = new Mock<IGameServer>();
        plain.SetupGet(s => s.Id).Returns(302);
        var operation = CreateOperation(local.Object, unreachable.Object, plain.Object);

        var result = await operation.ResetHealthAsync("flapper", CancellationToken.None).ConfigureAwait(false);

        Assert.That(result["serversNotChecked"]!.AsArray().Select(serverId => serverId!.GetValue<int>()), Is.EquivalentTo(new[] { 301, 302 }));
        Assert.That(result["summary"]!.GetValue<string>(), Does.Contain("not checked: 301, 302"));
    }

    /// <summary>
    /// An account whose bots could not be checked anywhere is not reported as having no bots, and the caller is
    /// told to retry instead of looking for another name.
    /// </summary>
    [Test]
    public void ReportsIncompleteChecksAsUnavailable()
    {
        var plain = new Mock<IGameServer>();
        plain.SetupGet(s => s.Id).Returns(302);
        var operation = CreateOperation(plain.Object);

        var exception = Assert.ThrowsAsync<AdminOperationException>(async () => await operation.ResetHealthAsync("flapper", CancellationToken.None).ConfigureAwait(false));

        Assert.That(exception!.Kind, Is.EqualTo(AdminOperationErrorKind.Unavailable));
        Assert.That(exception.Message, Does.Contain("302"));
        Assert.That(exception.Message, Does.Contain("could not be checked"));
        Assert.That(exception.Message, Does.Not.Contain("bots_list"));
    }

    private static BotWriteOperations CreateOperation(params object[] servers)
    {
        var provider = new Mock<IServerProvider>();
        provider.SetupGet(p => p.Servers).Returns(servers.Cast<IManageableServer>().ToList());
        return new BotWriteOperations(provider.Object, AdminOperationTestServices.CreateExecutor());
    }

    private static Mock<IGameServerAdministration> Server(int id, IReadOnlyList<LiveBotHealthReset> resets)
    {
        var server = new Mock<IGameServerAdministration>();
        server.As<IGameServer>().SetupGet(s => s.Id).Returns(id);
        server.Setup(s => s.ResetBotHealthAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(resets);
        return server;
    }
}
