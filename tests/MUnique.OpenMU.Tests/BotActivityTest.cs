// <copyright file="BotActivityTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests what a bot reports about itself to the administration surfaces - the panel column, the live player
/// projection and the <c>bots_list</c> tool all read <see cref="BotPlayer.Activity"/>.
/// </summary>
[TestFixture]
public class BotActivityTest
{
    /// <summary>
    /// A bot which isn't navigating reports nothing, so the surfaces show an empty cell instead of a state
    /// which belongs to no navigation. Its navigation only exists while it is in the world.
    /// </summary>
    [Test]
    public void ABotWhichIsNotNavigatingReportsNothing()
    {
        var bot = new BotPlayer(GameContextTestHelper.CreateGameContext());

        Assert.That(bot.Activity, Is.Null);
        Assert.That(bot.Activity?.Summary, Is.Null);
        Assert.That(bot.Activity?.Detail, Is.Null);
        Assert.That(bot.Activity?.State, Is.Null);
    }
}
