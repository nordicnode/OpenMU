// <copyright file="BotChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Globalization;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A chat command for game masters which shows what the bot chat of the game server did and
/// suppresses it during an incident: <c>/botchat</c> lists the messages which were granted,
/// throttled or muted per channel since the server started; <c>/botchat mute &lt;minutes&gt;</c>
/// silences every channel for the given time, and <c>/botchat unmute</c> ends that at once.
/// </summary>
/// <remarks>
/// The counts include the messages denied by a per-bot cooldown, a chance or a message budget,
/// so a sudden jump of throttled messages points at limits which are too tight for the population.
/// A mute window is server-wide; it is not part of the plugin configuration, so it also works
/// while somebody edits the configuration.
/// </remarks>
[PlugIn]
[Display(Name = nameof(PlugInResources.BotChatCommandPlugIn_Name), Description = nameof(PlugInResources.BotChatCommandPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("8B472352-5D27-4C58-A2E4-AD0083C9DEBE")]
[ChatCommandHelp(CommandKey, CharacterStatus.GameMaster)]
public class BotChatCommandPlugIn : IChatCommandPlugIn
{
    private const string CommandKey = "/botchat";

    /// <inheritdoc />
    public string Key => CommandKey;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.GameMaster;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        if (player.GameContext.FeaturePlugIns.GetPlugIn<BotChatPlugIn>() is not { } botChat)
        {
            await player.ShowBlueMessageAsync("The bot chat plugin is not active on this game server.").ConfigureAwait(false);
            return;
        }

        var arguments = command.Length > CommandKey.Length ? command[CommandKey.Length..].Trim() : string.Empty;
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var throttle = botChat.GetThrottle(player.GameContext);
        if (parts.Length == 0 || parts[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            await ShowStatisticsAsync(player, throttle, botChat.Configuration).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("mute", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || minutes <= 0)
            {
                await ShowUsageAsync(player).ConfigureAwait(false);
                return;
            }

            var until = throttle.Mute(DateTime.UtcNow, TimeSpan.FromMinutes(minutes));
            await player.ShowBlueMessageAsync($"All bot chat is muted until {until:u} (in {minutes} minute(s)).").ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("unmute", StringComparison.OrdinalIgnoreCase))
        {
            throttle.Unmute();
            await player.ShowBlueMessageAsync("All bot chat is unmuted.").ConfigureAwait(false);
            return;
        }

        await ShowUsageAsync(player).ConfigureAwait(false);
    }

    private static async ValueTask ShowStatisticsAsync(Player player, BotChatThrottle throttle, BotChatConfiguration? configuration)
    {
        var now = DateTime.UtcNow;
        var statistics = throttle.GetStatistics(now);
        await player.ShowBlueMessageAsync("Bot chat of this game server since it started:").ConfigureAwait(false);
        foreach (var (channel, count) in statistics.Channels)
        {
            await player.ShowBlueMessageAsync(
                $"  {channel.ToString().ToLowerInvariant()}: {count.Granted} granted, {count.Throttled} throttled, {count.Muted} muted.").ConfigureAwait(false);
        }

        await player.ShowBlueMessageAsync(
            $"  total: {statistics.Granted} granted, {statistics.Throttled} throttled, {statistics.Muted} muted, {statistics.SuppressedOccasions} occasions suppressed.").ConfigureAwait(false);

        if (configuration is { } config)
        {
            await player.ShowBlueMessageAsync(
                $"  budgets: {DescribeBudget(config.GlobalMessagesPerMinute)} in total, {DescribeBudget(config.ChannelMessagesPerMinute)} per channel.").ConfigureAwait(false);
        }

        await player.ShowBlueMessageAsync(
            statistics.MutedUntilUtc is { } mutedUntil
                ? $"  muted until {mutedUntil:u}; use {CommandKey} unmute to end it."
                : $"  not muted; use {CommandKey} mute <minutes> to mute all channels.").ConfigureAwait(false);
    }

    private static string DescribeBudget(int messagesPerMinute)
        => messagesPerMinute > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{messagesPerMinute}/min")
            : "unlimited";

    private static ValueTask ShowUsageAsync(Player player)
        => player.ShowBlueMessageAsync($"Usage: {CommandKey} [status] | {CommandKey} mute <minutes> | {CommandKey} unmute");
}
