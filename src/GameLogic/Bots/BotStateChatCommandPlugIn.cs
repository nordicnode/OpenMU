// <copyright file="BotStateChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.IO;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A chat command for game masters which explains what a server-side bot is doing right now:
/// <c>/botstate &lt;bot&gt;</c> - its state, since when, why, where its time went and the transitions it took
/// before. It answers the questions a wedged or idle bot raises ("why doesn't it hunt?", "why doesn't it join
/// the event?") without having to read its log.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.BotStateChatCommandPlugIn_Name), Description = nameof(PlugInResources.BotStateChatCommandPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("4B1F7E73-9C1E-4E8B-9B57-2C8A6E1D3F60")]
[ChatCommandHelp(CommandKey, CharacterStatus.GameMaster)]
public class BotStateChatCommandPlugIn : IChatCommandPlugIn
{
    private const string CommandKey = "/botstate";

    /// <inheritdoc />
    public string Key => CommandKey;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.GameMaster;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        var botName = command.Length > CommandKey.Length ? command[CommandKey.Length..].Trim() : string.Empty;
        if (botName.Length == 0)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.CharacterNameIsRequired)).ConfigureAwait(false);
            return;
        }

        if (player.GameContext.GetPlayerByCharacterName(botName) is not BotPlayer bot)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.CharacterNotFound), botName).ConfigureAwait(false);
            return;
        }

        if (bot.Activity is not { } activity)
        {
            await player.ShowBlueMessageAsync($"{bot.Name} is not navigating (it is not in the world).").ConfigureAwait(false);
            return;
        }

        using var reader = new StringReader(activity.Detail);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await player.ShowBlueMessageAsync(line).ConfigureAwait(false);
        }

        // Whether the bot is merely busy or actually broken: how often its ticks failed, why, and how often it
        // was restarted to heal it - the failure policy is BotHealthPolicy, the record is the account's.
        if (bot.Health.Describe() is { } health)
        {
            await player.ShowBlueMessageAsync(health).ConfigureAwait(false);
        }
    }
}
