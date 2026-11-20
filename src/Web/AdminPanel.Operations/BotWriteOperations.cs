// <copyright file="BotWriteOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>
/// The writes an operator has on the server-side bots: forgetting the failures of an account, so a bot which was
/// quarantined is allowed to run and to be restarted again.
/// </summary>
/// <remarks>
/// There is deliberately nothing here which starts, stops or respawns a bot - those are bot lifecycle decisions of
/// the game logic, taken from the bot's own configuration. A health reset only clears what the server counts
/// against an account; the policy behind it (see the game logic's BotHealthPolicy) stays as it is.
/// </remarks>
/// <param name="servers">The server registry.</param>
/// <param name="executor">The authorization and audit service.</param>
public sealed class BotWriteOperations(IServerProvider servers, AdminOperationExecutor executor)
{
    /// <summary>
    /// Forgets the failures, the restarts and the quarantine of every bot of the given account.
    /// </summary>
    /// <param name="account">The exact account login name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was cleared, per bot of the account.</returns>
    [AdminOperation(AdminRole.Operator, ReadOnly = false)]
    public ValueTask<JsonObject> ResetHealthAsync(string account, CancellationToken cancellationToken)
        => executor.ExecuteAsync(
            AdminRoles.Operator,
            "bots_reset_health",
            new { account },
            async _ =>
            {
                if (string.IsNullOrWhiteSpace(account))
                {
                    throw new AdminOperationException("Supply the account login name whose bot health should be reset.", AdminOperationErrorKind.InvalidInput);
                }

                var cleared = new List<object>();
                var remoteServers = servers.Servers
                    .Where(s => s is IGameServerAdministration && s is not IGameServerContextProvider)
                    .Select(s => s.Id)
                    .ToArray();
                foreach (var server in servers.Servers.OfType<IGameServerContextProvider>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var serverId = ((IManageableServer)server).Id;
                    var bots = (await server.Context.GetPlayersAsync().ConfigureAwait(false)).OfType<BotPlayer>().ToList();
                    var resetSlots = new HashSet<byte>();
                    if (server.Context.FeaturePlugIns?.GetPlugIn<BotFeaturePlugIn>() is { } feature)
                    {
                        foreach (var record in feature.ResetHealth(server.Context, account))
                        {
                            resetSlots.Add(record.Slot);
                            var matchingBot = bots.FirstOrDefault(bot => string.Equals(bot.Account?.LoginName, account, StringComparison.OrdinalIgnoreCase)
                                && bot.SelectedCharacter?.CharacterSlot == record.Slot);
                            cleared.Add(new
                            {
                                serverId,
                                character = matchingBot?.SelectedCharacter?.Name,
                                characterSlot = (int?)record.Slot,
                                restarts = record.Restarts,
                                faults = record.Faults,
                                quarantinedSeconds = record.QuarantinedSeconds,
                            });
                        }
                    }

                    foreach (var bot in bots)
                    {
                        if (!string.Equals(bot.Account?.LoginName, account, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (bot.SelectedCharacter?.CharacterSlot is { } slot && resetSlots.Contains(slot))
                        {
                            continue;
                        }

                        // Directly constructed bots need not belong to the feature's manager.
                        var activity = bot.Activity;
                        var quarantinedSeconds = activity?.QuarantinedUntilUtc is { } until
                            ? (int)Math.Max(0, (until - DateTimeOffset.UtcNow).TotalSeconds)
                            : 0;
                        cleared.Add(new
                        {
                            serverId,
                            character = bot.SelectedCharacter?.Name,
                            characterSlot = (int?)bot.SelectedCharacter?.CharacterSlot,
                            restarts = activity?.Restarts ?? 0,
                            faults = activity?.Failures ?? 0,
                            quarantinedSeconds,
                        });
                        bot.ResetHealth();
                    }
                }

                if (cleared.Count == 0)
                {
                    throw new AdminResourceNotFoundException(
                        $"No local bot health record was found for the account '{account}'. Use bots_list to find an active bot's exact login name."
                        + (remoteServers.Length > 0 ? " Remote game servers cannot be reset by this operation." : string.Empty),
                        nameof(account));
                }

                return AdminReadResult.From(new
                {
                    account,
                    bots = cleared,
                    remoteServersNotChecked = remoteServers,
                    summary = $"The failures and quarantines of {cleared.Count} local bot(s) of '{account}' are forgotten. A bot which still fails keeps asking for a restart, which the next maintenance pass acts on."
                        + (remoteServers.Length > 0 ? " Remote game servers were not checked or reset." : string.Empty),
                });
            },
            cancellationToken);
}
