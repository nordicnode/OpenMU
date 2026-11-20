// <copyright file="BotDuelPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Duel;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Answers the duel requests which players send to a server-side bot: the bot accepts a fair challenge
/// when the operator enabled duels (<see cref="BotConfiguration.Duels"/>), and declines everything else
/// like a player clicking "no" - a request which is never answered leaves the player waiting.
/// The answer is given in the bot's next MU Helper tick, because accepting warps both duelists; the
/// actual decision is <see cref="BotDuelHandler.CanAccept"/>.
/// </summary>
/// <remarks>
/// This runs for every bot and doesn't depend on the optional bot chat, which only adds the line that
/// explains a refusal. Without that separation, <see cref="BotConfiguration.Duels"/> alone never
/// produced a single duel, because the only listener for the request lived in the chat plugin.
/// </remarks>
[PlugIn]
[Display(Name = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotDuelPlugIn_Name), Description = nameof(MUnique.OpenMU.GameLogic.Properties.PlugInResources.BotDuelPlugIn_Description), ResourceType = typeof(MUnique.OpenMU.GameLogic.Properties.PlugInResources))]
[Guid("D208D481-0941-41FF-A35F-3F4E38DB2BD5")]
public class BotDuelPlugIn : IDuelRequestedPlugIn
{
    /// <inheritdoc />
    public ValueTask DuelRequestedAsync(Player requester, Player target)
    {
        if (target is not OfflinePlayer bot
            || bot.Account?.IsBot != true
            || requester is OfflinePlayer
            || ReferenceEquals(requester, target))
        {
            return ValueTask.CompletedTask;
        }

        bot.PendingBotActions.Enqueue(async () =>
        {
            if (await BotDuelHandler.TryAcceptAsync(bot, requester).ConfigureAwait(false))
            {
                return;
            }

            // Still the request the player sent: answer it, so it doesn't stay open forever.
            if (bot.DuelRoom is { State: DuelState.DuelRequested } room
                && ReferenceEquals(room.Requester, requester))
            {
                await new DuelActions().HandleDuelResponseAsync(bot, requester, false).ConfigureAwait(false);
            }
        });

        return ValueTask.CompletedTask;
    }
}
