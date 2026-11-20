// <copyright file="BotChatSettingsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Components;

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Web.Shared.Components.Form;
using MUnique.OpenMU.Web.Shared.Components.Modal;
using MUnique.OpenMU.Web.Shared.Services;

/// <summary>
/// Tests the auto-generated settings form of the bot chat plugin.
/// </summary>
[TestFixture]
public class BotChatSettingsTests
{
    /// <summary>
    /// The recent-line memory is shown and editable, and every topic has an inline cooldown row.
    /// </summary>
    [Test]
    public void SettingsShowRecentLineMemoryAndPerTopicCooldowns()
    {
        using var context = CreateContext();
        var configuration = new BotChatConfiguration();
        var component = context.Render<AutoForm<BotChatConfiguration>>(parameters => parameters
            .Add(form => form.Model, configuration));

        Assert.That(component.FindAll("label").Select(label => label.TextContent).Any(text => text.Contains("Recent-line memory", StringComparison.Ordinal)), Is.True);

        var memory = component.Find("#RecentLineMemory");
        Assert.That(memory.GetAttribute("value"), Is.EqualTo("4"));
        memory.Change("7");
        Assert.That(configuration.RecentLineMemory, Is.EqualTo(7));

        var topicCard = component.FindAll(".card")
            .Single(card => card.QuerySelector("h5")?.TextContent.Contains("Cooldowns per topic", StringComparison.Ordinal) == true);

        // The table starts collapsed once it holds more than ten topics, and the
        // eleventh topic tipped it over that limit, so expand it first, like an
        // operator would, before checking its rows. When it holds ten or fewer
        // topics there is no expand button and the rows are shown right away.
        topicCard.QuerySelector("button.btn-outline-info")?.Click();
        topicCard = component.FindAll(".card")
            .Single(card => card.QuerySelector("h5")?.TextContent.Contains("Cooldowns per topic", StringComparison.Ordinal) == true);

        var rows = topicCard.QuerySelectorAll("tbody tr");
        Assert.That(rows.Length, Is.EqualTo(Enum.GetValues<BotChatCategory>().Length));
        Assert.That(topicCard.QuerySelectorAll("form").Length, Is.Zero);
        Assert.That(rows.Select(row => row.QuerySelector("select")?.QuerySelectorAll("option").Length), Is.All.EqualTo(Enum.GetValues<BotChatCategory>().Length));
        Assert.That(rows.Select(row => row.QuerySelector("input[type=number]")?.GetAttribute("value")), Is.All.EqualTo("300000"));
        Assert.That(topicCard.QuerySelectorAll("a.btn").Length, Is.Zero);

        rows[0].QuerySelector("input[type=number]")!.Change("600000");
        Assert.That(configuration.TopicCooldowns.Count(entry => entry.Cooldown == TimeSpan.FromMinutes(10)), Is.EqualTo(1));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddOptions();
        context.Services.AddLocalization();
        context.Services.AddSingleton<IChangeNotificationService, ChangeNotificationService>();
        context.Services.AddSingleton<IModalService, ModalService>();
        context.Services.AddLogging();
        return context;
    }
}
