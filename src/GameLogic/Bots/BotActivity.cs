// <copyright file="BotActivity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// What a server-side bot is doing right now, in the shape the administration surfaces report it: the
/// mode, why it is in it and for how long - so that a bot which is wedged, idle or stuck on an errand can
/// be told apart from one which is busy.
/// </summary>
/// <param name="State">The mode the bot is in, e.g. <c>Hunt</c>.</param>
/// <param name="Reason">Why the navigation tick put the bot into that mode, e.g. <c>monsters nearby</c>.</param>
/// <param name="TimeInState">How long the bot has already been in that mode.</param>
/// <param name="Summary">The mode, its duration and its reason in one line.</param>
/// <param name="Detail">The long form: where the bot's time went, its recent transitions and its guard violations.</param>
/// <param name="Failures">How many ticks failed so far, over all modes.</param>
/// <param name="LastFailure">Why the last failed tick failed, or <c>null</c> while no tick failed.</param>
/// <param name="Restarts">How many times the account was restarted to heal a bot whose ticks kept failing.</param>
/// <param name="QuarantinedUntilUtc">Until when the bot is kept from being restarted because restarts did not
/// help, or <c>null</c> while it may be restarted.</param>
public sealed record BotActivity(
    string State,
    string Reason,
    TimeSpan TimeInState,
    string Summary,
    string Detail,
    int Failures = 0,
    string? LastFailure = null,
    int Restarts = 0,
    DateTimeOffset? QuarantinedUntilUtc = null);
