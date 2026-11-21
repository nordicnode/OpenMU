// <copyright file="BotWriteOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Text.Json.Nodes;
using System.Threading;
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
/// The reset goes through the live administration channel, which also reaches remote game servers behind Dapr.
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
    /// <returns>What was cleared, per bot of the account, and the servers which could not be checked.</returns>
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
                var notChecked = new List<int>();
                foreach (var server in servers.Servers.OfType<IGameServer>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (server is not IGameServerAdministration administration)
                    {
                        notChecked.Add(server.Id);
                        continue;
                    }

                    IReadOnlyList<LiveBotHealthReset> resets;
                    try
                    {
                        resets = await administration.ResetBotHealthAsync(account, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // A stopped or unreachable server is reported instead of silently skipped.
                        notChecked.Add(server.Id);
                        continue;
                    }

                    cleared.AddRange(resets.Select(reset => new
                    {
                        serverId = server.Id,
                        character = reset.Character,
                        characterSlot = reset.CharacterSlot,
                        restarts = reset.Restarts,
                        faults = reset.Faults,
                        quarantinedSeconds = reset.QuarantinedSeconds,
                    }));
                }

                if (cleared.Count == 0)
                {
                    throw new AdminResourceNotFoundException(
                        $"No bot health record was found for the account '{account}'. Use bots_list to find an active bot's exact login name."
                        + (notChecked.Count > 0 ? $" Game servers not checked: {string.Join(", ", notChecked)}." : string.Empty),
                        nameof(account));
                }

                return AdminReadResult.From(new
                {
                    account,
                    bots = cleared,
                    serversNotChecked = notChecked,
                    summary = $"The failures and quarantines of {cleared.Count} bot(s) of '{account}' are forgotten. A bot which still fails keeps asking for a restart, which the next maintenance pass acts on."
                        + (notChecked.Count > 0 ? $" Game servers not checked: {string.Join(", ", notChecked)}." : string.Empty),
                });
            },
            cancellationToken);
}
