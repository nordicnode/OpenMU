// <copyright file="BotMeetingOutcomeTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// Tests how the administration surfaces word the outcome of an event meeting which is over.
/// </summary>
[TestFixture]
public class BotMeetingOutcomeTest
{
    /// <summary>
    /// The one-line form says what became of the meeting and where it happened, so the bot table can show it
    /// after the meeting itself is gone.
    /// </summary>
    /// <param name="state">The terminal meeting state.</param>
    /// <param name="expected">The expected one-line summary.</param>
    [TestCase(nameof(BotMeetingState.Joined), "Last meeting: joined at Devias 215, 40")]
    [TestCase(nameof(BotMeetingState.Expired), "Last meeting: expired at Devias 215, 40")]
    [TestCase(nameof(BotMeetingState.Abandoned), "Last meeting: abandoned at Devias 215, 40")]
    public void SummarizesTheOutcome(string state, string expected)
    {
        var outcome = new BotMeetingOutcome(state, "Devias 215, 40", "Blood Castle", null);

        Assert.That(outcome.Summary, Is.EqualTo(expected));
    }

    /// <summary>
    /// The long form names the event and why the bot gave the meeting up, so a tooltip can answer the question
    /// the one-line form raises.
    /// </summary>
    [Test]
    public void ExplainsAnAbandonedMeeting()
    {
        var outcome = new BotMeetingOutcome(
            nameof(BotMeetingState.Abandoned),
            "Lost Tower 200, 200",
            "Blood Castle",
            "no route to the entry NPC and no safezone to wait in");

        Assert.That(
            outcome.Detail,
            Is.EqualTo("last meeting abandoned at Lost Tower 200, 200 (Blood Castle) - no route to the entry NPC and no safezone to wait in"));
    }

    /// <summary>
    /// A meeting which ended without a separate reason doesn't get a dangling separator - expired and joined
    /// already say everything.
    /// </summary>
    [Test]
    public void OmitsAMissingReason()
    {
        var outcome = new BotMeetingOutcome(nameof(BotMeetingState.Expired), "Devias 215, 40", null, null);

        Assert.That(outcome.Detail, Is.EqualTo("last meeting expired at Devias 215, 40"));
    }
}
