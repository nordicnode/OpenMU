// <copyright file="BotStuckAlarmBannerTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Components;

using Bunit;
using MUnique.OpenMU.Web.AdminPanel.Components.Dashboard;

/// <summary>
/// Tests the banner which tells an operator that bots wait too long, on the dashboard and in the bots tab.
/// </summary>
[TestFixture]
public class BotStuckAlarmBannerTests
{
    /// <summary>
    /// The alarm is shown with the sentence the operations layer composed, as a status region so a screen
    /// reader announces it.
    /// </summary>
    [Test]
    public void ShowsTheAlarmSentence()
    {
        using var context = new BunitContext();

        var component = context.Render<BotStuckAlarmBanner>(parameters => parameters
            .Add(banner => banner.Line, "3 of 20 bots wait for more than 10 minutes: Idle 2, Shopping 1."));

        var alert = component.Find("#bots-stuck-alarm");
        Assert.That(alert.TextContent, Is.EqualTo("3 of 20 bots wait for more than 10 minutes: Idle 2, Shopping 1."));
        Assert.That(alert.GetAttribute("role"), Is.EqualTo("status"));
        Assert.That(alert.ClassList, Does.Contain("alert-warning"));
    }

    /// <summary>
    /// A healthy server renders nothing at all, so the pages which host the banner keep their layout without a
    /// gap or an empty alert box.
    /// </summary>
    [Test]
    public void RendersNothingWithoutAnAlarm()
    {
        using var context = new BunitContext();

        var withoutLine = context.Render<BotStuckAlarmBanner>(parameters => parameters.Add(banner => banner.Line, (string?)null));
        var withEmptyLine = context.Render<BotStuckAlarmBanner>(parameters => parameters.Add(banner => banner.Line, string.Empty));

        Assert.That(withoutLine.FindAll("#bots-stuck-alarm"), Is.Empty);
        Assert.That(withEmptyLine.FindAll("#bots-stuck-alarm"), Is.Empty);
    }
}
