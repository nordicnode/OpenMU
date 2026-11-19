// <copyright file="BotMarketPanelTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Components;

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Web.AdminPanel.Components.Bots;
using MUnique.OpenMU.Web.Shared.Services;

/// <summary>
/// Tests the market panel above the bot table: the counters and guardrails of each server, and that a server
/// without the merchant plugin doesn't leave an empty card behind.
/// </summary>
[TestFixture]
public class BotMarketPanelTests
{
    /// <summary>
    /// The panel shows the activity, the price classes and the guardrails of a server in one place.
    /// </summary>
    [Test]
    public void ShowsTheMarketOfTheServer()
    {
        using var context = CreateContext([new BotMarketServerStatus(1, "Loren Market", Market())]);

        var panel = context.Render<BotMarketPanel>();

        panel.WaitForAssertion(() =>
        {
            Assert.That(panel.Markup, Does.Contain("Bot market"));
            Assert.That(panel.Markup, Does.Contain("Loren Market"));
            Assert.That(panel.Markup, Does.Contain("3 store(s) open with 12 priced good(s) on offer."));
            Assert.That(panel.Markup, Does.Contain("4 listed, 2 sold, 1 retired"));
            Assert.That(panel.Markup, Does.Contain("Price bands"));
            Assert.That(panel.Markup, Does.Contain("cheap: 8 listed, average"));
            Assert.That(panel.Markup, Does.Contain("expensive: 2 listed, prices in jewels."));
            Assert.That(panel.Markup, Does.Contain("Guardrails: zen"));
            Assert.That(panel.Markup, Does.Contain("20 good(s) per store, 400 on the market, 50 store(s)."));
        });
    }

    /// <summary>
    /// Without an active merchant plugin there is no market to show, so the panel stays out of the page.
    /// </summary>
    [Test]
    public void ShowsNothingWithoutAMarket()
    {
        using var context = CreateContext([]);

        var panel = context.Render<BotMarketPanel>();

        panel.WaitForAssertion(() => Assert.That(panel.Markup, Does.Not.Contain("Bot market")));
    }

    private static BunitContext CreateContext(IReadOnlyList<BotMarketServerStatus> statuses)
    {
        var service = new Mock<IBotMarketService>();
        service.Setup(s => s.GetMarketStatusesAsync()).ReturnsAsync(statuses);
        var context = new BunitContext();
        context.Services.AddSingleton(service.Object);
        context.Services.AddSingleton(Mock.Of<ILogger<BotMarketPanel>>());
        return context;
    }

    private static BotMarketStatus Market()
        => new(
            OpenStores: 3,
            ListedGoods: 12,
            ListedLastHour: 4,
            SoldLastHour: 2,
            RetiredLastHour: 1,
            ZenLastHour: 9_500_000,
            ListedTotal: 20,
            SoldTotal: 7,
            RetiredTotal: 2,
            ZenTotal: 48_000_000,
            PaidItemsTotal: 5,
            PriceClasses:
            [
                new BotMarketClassStatus("Cheap", 8, 6, 1_000_000, 3_000_000, 2_000_000),
                new BotMarketClassStatus("Expensive", 2, 0, 0, 0, 0),
            ],
            MinimumZenPrice: 100_000,
            MaximumZenPrice: 100_000_000,
            MaximumGoodsPerStore: 20,
            MaximumListedGoods: 400,
            MaximumMerchants: 50);
}
