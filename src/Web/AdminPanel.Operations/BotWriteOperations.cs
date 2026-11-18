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
                foreach (var server in servers.Servers.OfType<IGameServerContextProvider>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var serverId = (byte)((IManageableServer)server).Id;
                    foreach (var bot in (await server.Context.GetPlayersAsync().ConfigureAwait(false)).OfType<BotPlayer>())
                    {
                        if (!string.Equals(bot.Account?.LoginName, account, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Captured before the reset - afterwards there is nothing left to report about it.
                        var activity = bot.Activity;
                        var quarantinedSeconds = activity?.QuarantinedUntilUtc is { } until
                            ? (int)Math.Max(0, (until - DateTimeOffset.UtcNow).TotalSeconds)
                            : 0;
                        cleared.Add(new
                        {
                            serverId,
                            character = bot.SelectedCharacter?.Name,
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
                        $"No bot is running for the account '{account}'. Use bots_list to find its exact login name.",
                        nameof(account));
                }

                return AdminReadResult.From(new
                {
                    account,
                    bots = cleared,
                    summary = $"The failures and quarantines of {cleared.Count} bot(s) of '{account}' are forgotten. A bot which still fails keeps asking for a restart, which the next maintenance pass acts on.",
                });
            },
            cancellationToken);
}
