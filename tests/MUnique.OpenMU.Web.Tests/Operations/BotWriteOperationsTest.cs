// <copyright file="BotWriteOperationsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Operations;

using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.AdminAuth;
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
    /// The account name is matched case-insensitively, because it comes from an operator who read it from a list,
    /// and the answer names the bots it acted on instead of only claiming success.
    /// </summary>
    [Test]
    public async Task ResetsTheBotsOfTheAccountItWasGivenAsync()
    {
        var bot = CreateBot("Flapper");
        var operation = CreateOperation(bot);

        var result = await operation.ResetHealthAsync("flapper", CancellationToken.None).ConfigureAwait(false);

        Assert.That(result["bots"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(result["summary"]!.GetValue<string>(), Does.Contain("forgotten"));
        Assert.That(result["bots"]!.AsArray()[0]!["quarantinedSeconds"]!.GetValue<int>(), Is.Zero);
    }

    private static BotWriteOperations CreateOperation(params BotPlayer[] bots)
    {
        var gameContext = new Mock<IGameServerContext>();
        SetupGameContext(gameContext);
        gameContext.Setup(c => c.GetPlayersAsync()).ReturnsAsync(bots.Cast<Player>().ToList());

        var server = new Mock<IManageableServer>();
        server.SetupGet(s => s.Id).Returns(0);
        server.As<IGameServerContextProvider>().SetupGet(p => p.Context).Returns(gameContext.Object);

        var servers = new Mock<IServerProvider>();
        servers.SetupGet(p => p.Servers).Returns(new List<IManageableServer> { server.Object });

        return new BotWriteOperations(servers.Object, AdminOperationTestServices.CreateExecutor());
    }

    private static BotPlayer CreateBot(string accountName)
    {
        var gameContext = new Mock<IGameContext>();
        SetupGameContext(gameContext);
        var bot = new BotPlayer(gameContext.Object) { Account = new DataModel.Entities.Account { LoginName = accountName } };
        return bot;
    }

    private static void SetupGameContext<T>(Mock<T> gameContext)
        where T : class, IGameContext
    {
        var asContext = gameContext.As<IGameContext>();
        asContext.SetupGet(c => c.LoggerFactory).Returns(NullLoggerFactory.Instance);
        asContext.SetupGet(c => c.Configuration).Returns(new DataModel.Configuration.GameConfiguration());
        var persistence = new Mock<Persistence.IPersistenceContextProvider>();
        persistence.Setup(p => p.CreateNewPlayerContext(It.IsAny<DataModel.Configuration.GameConfiguration>())).Returns(Mock.Of<Persistence.IPlayerContext>());
        asContext.SetupGet(c => c.PersistenceContextProvider).Returns(persistence.Object);
    }
}
