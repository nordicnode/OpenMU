// <copyright file="BotChatTiming.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.Interfaces;

/// <summary>
/// The delays which make a bot's chat look human: when a reaction comes, how long typing takes, and
/// which time of day a line belongs to. Pure logic - exposed for unit tests.
/// </summary>
internal static class BotChatTiming
{
    /// <summary>
    /// The delay range of a level milestone comment: a moment after the level-up effect.
    /// </summary>
    internal static readonly (int Min, int Max) LevelMilestoneDelaySeconds = (1, 4);

    /// <summary>
    /// The delay range of a greeting: the entering player's client needs a moment to load the world.
    /// </summary>
    internal static readonly (int Min, int Max) GreetingDelaySeconds = (3, 8);

    /// <summary>
    /// The delay range of a comment on being killed: said while the bot still lies there, before it
    /// respawns, so the killer sees it.
    /// </summary>
    internal static readonly (int Min, int Max) KilledDelaySeconds = (1, 2);

    /// <summary>
    /// The delay range of an event reminder in town: the players read the server's own announcement
    /// first.
    /// </summary>
    internal static readonly (int Min, int Max) EventReminderDelaySeconds = (10, 40);

    /// <summary>
    /// The delay range of calling out a notable monster: the time to notice it.
    /// </summary>
    internal static readonly (int Min, int Max) NotableMonsterDelaySeconds = (1, 4);

    /// <summary>
    /// The delay range of a congratulation: the level-up or reset effect first, then the "gz".
    /// </summary>
    internal static readonly (int Min, int Max) CongratulationDelaySeconds = (2, 5);

    /// <summary>
    /// The delay range before a bot starts typing an answer: the time to notice and read the message.
    /// </summary>
    internal static readonly (int Min, int Max) TypingReactionSeconds = (1, 3);

    /// <summary>
    /// The range of the time a bot needs per typed character - a casual typist in a game chat.
    /// </summary>
    internal static readonly (int Min, int Max) TypingMillisecondsPerCharacter = (120, 220);

    /// <summary>
    /// The gap between the whispers of two bots answering the same party search, so the answers
    /// trickle in like those of players who read the post at different moments.
    /// </summary>
    internal static readonly (int Min, int Max) PartyVolunteerSpacingSeconds = (2, 6);

    /// <summary>
    /// The longest a bot types, so a long line doesn't arrive after the player gave up waiting.
    /// </summary>
    private static readonly TimeSpan MaximumTypingTime = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Gets the time of day of an hour of the server's local time (see <see cref="BotChatTimeOfDay"/>).
    /// </summary>
    /// <param name="hour">The hour, 0 to 23.</param>
    /// <returns>The time of day.</returns>
    internal static BotChatTimeOfDay GetTimeOfDay(int hour) => hour switch
    {
        >= 5 and < 11 => BotChatTimeOfDay.Morning,
        >= 11 and < 18 => BotChatTimeOfDay.Day,
        >= 18 and < 23 => BotChatTimeOfDay.Evening,
        _ => BotChatTimeOfDay.Night,
    };

    /// <summary>
    /// Gets the time a bot needs to answer with a text of the given length: the reaction, then the
    /// typing of each character - "np" comes quickly, a whole sentence takes a while.
    /// </summary>
    /// <param name="length">The length of the text.</param>
    /// <param name="reaction">The time to notice and read the message.</param>
    /// <param name="perCharacter">The time per typed character.</param>
    /// <returns>The delay, at most <see cref="MaximumTypingTime"/>.</returns>
    internal static TimeSpan GetTypingTime(int length, TimeSpan reaction, TimeSpan perCharacter)
    {
        var time = reaction + (perCharacter * length);
        return time < MaximumTypingTime ? time : MaximumTypingTime;
    }

    /// <summary>
    /// Gets a random, human-like time to answer with the text (see <see cref="GetTypingTime"/>). The
    /// English text stands in for all translations - they are about as long.
    /// </summary>
    /// <param name="text">The text of the answer.</param>
    /// <returns>The typing delay.</returns>
    internal static TimeSpan TypingDelay(LocalizedString text)
        => GetTypingTime(
            text.ValueInNeutralLanguage.Length,
            RandomDelay(TypingReactionSeconds),
            TimeSpan.FromMilliseconds(Rand.NextInt(TypingMillisecondsPerCharacter.Min, TypingMillisecondsPerCharacter.Max)));

    /// <summary>
    /// Draws a random delay between the bounds.
    /// </summary>
    /// <param name="min">The inclusive minimum.</param>
    /// <param name="max">The exclusive maximum.</param>
    /// <returns>The delay.</returns>
    internal static TimeSpan RandomDelay(TimeSpan min, TimeSpan max)
        => max <= min ? min : min + TimeSpan.FromMilliseconds(Rand.NextDouble() * (max - min).TotalMilliseconds);

    /// <summary>
    /// Draws a random delay between the bounds, in seconds.
    /// </summary>
    /// <param name="seconds">The inclusive minimum and exclusive maximum, in seconds.</param>
    /// <returns>The delay.</returns>
    internal static TimeSpan RandomDelay((int Min, int Max) seconds)
        => RandomDelay(TimeSpan.FromSeconds(seconds.Min), TimeSpan.FromSeconds(seconds.Max));
}
