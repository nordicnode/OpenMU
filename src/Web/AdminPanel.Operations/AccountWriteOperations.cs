// <copyright file="AccountWriteOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.ComponentModel.DataAnnotations;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Web.AdminPanel.API;

/// <summary>Creates accounts and changes offline accounts without exposing credentials.</summary>
/// <param name="persistence">The persistence provider.</param>
/// <param name="configurations">The shared configuration source.</param>
/// <param name="login">The login server.</param>
/// <param name="executor">The authorization and audit service.</param>
public sealed class AccountWriteOperations(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations, ILoginServer login, AdminOperationExecutor executor)
{
    /// <summary>Creates a normal account using the public registration API's validation.</summary>
    /// <param name="loginName">The new login name.</param>
    /// <param name="password">The initial password; never recorded in the audit trail.</param>
    /// <param name="email">The optional email address.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the account was created or already exists.</returns>
    [AdminOperation(AdminRole.Operator, ReadOnly = false)]
    public ValueTask<JsonObject> CreateAsync(string loginName, string password, string? email, CancellationToken cancellationToken)
        => executor.ExecuteAsync(
            AdminRoles.Operator,
            "accounts_create",
            new { loginName, email },
            async _ =>
            {
                var registration = new AccountRegistration { LoginName = loginName, Password = password, EMail = email };
                var validation = new List<ValidationResult>();
                if (!Validator.TryValidateObject(registration, new ValidationContext(registration), validation, true))
                {
                    throw new AdminOperationException($"Use a printable ASCII login of 3-10 characters, a printable ASCII {nameof(password)} of 3-20 characters and a valid optional email address.", AdminOperationErrorKind.InvalidInput);
                }

                var config = await configurations.GetOwnerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                using var context = persistence.CreateNewPlayerContext(config);
                if (await context.GetAccountByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return AdminReadResult.From(new { status = "already_exists", loginName });
                }

                var account = context.CreateNew<Account>();
                account.LoginName = loginName;
                account.EMail = email ?? string.Empty;
                account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                account.State = AccountState.Normal;
                account.RegistrationDate = DateTime.UtcNow;
                try
                {
                    await SaveAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (PersistenceFailures.IsRejectedSave(exception))
                {
                    using var check = persistence.CreateNewPlayerContext(config);
                    if (await check.GetAccountByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        return AdminReadResult.From(new { status = "already_exists", loginName });
                    }

                    throw;
                }

                return AdminReadResult.From(new { status = "created", loginName });
            },
            cancellationToken);

    /// <summary>Changes an offline account's state with a mandatory audit reason.</summary>
    /// <param name="loginName">The exact login name.</param>
    /// <param name="state">The requested state.</param>
    /// <param name="reason">The audit reason.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new state.</returns>
    [AdminOperation(AdminRole.Operator, ReadOnly = false)]
    public ValueTask<JsonObject> SetStateAsync(string loginName, AccountState state, string reason, CancellationToken cancellationToken)
        => executor.ExecuteAsync(
            AdminRoles.Operator,
            "accounts_set_state",
            new { loginName, state = state.ToString(), reason },
            async _ =>
            {
                ValidateReason(reason);
                if (!Enum.IsDefined(state))
                {
                    throw new AdminOperationException("Unknown account state.", AdminOperationErrorKind.InvalidInput);
                }

                var config = await configurations.GetOwnerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                using var check = persistence.CreateNewPlayerContext(config);
                var checkedAccount = await check.GetAccountByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound(loginName);
                await this.RequireOfflineAsync(checkedAccount.LoginName).ConfigureAwait(false);
                if (IsPrivileged(checkedAccount.State) || IsPrivileged(state))
                {
                    await executor.RequireAsync(AdminRoles.Administrator, cancellationToken).ConfigureAwait(false);
                }

                return await this.ChangeOfflineAccountAsync(
                    checkedAccount.LoginName,
                    config,
                    async (account, context) =>
                    {
                        if (IsPrivileged(account.State) || IsPrivileged(state))
                        {
                            await executor.RequireAsync(AdminRoles.Administrator, cancellationToken).ConfigureAwait(false);
                        }

                        account.State = state;
                        await SaveAsync(context, cancellationToken).ConfigureAwait(false);
                        return AdminReadResult.From(new { loginName = account.LoginName, state = state.ToString() });
                    },
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Resets an offline account's password without returning or auditing the secret.</summary>
    /// <param name="loginName">The exact login name.</param>
    /// <param name="password">The new password.</param>
    /// <param name="reason">The audit reason.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A success status with no credentials.</returns>
    [AdminOperation(AdminRole.Operator, ReadOnly = false)]
    public ValueTask<JsonObject> ResetPasswordAsync(string loginName, string password, string reason, CancellationToken cancellationToken)
        => executor.ExecuteAsync(
            AdminRoles.Operator,
            "accounts_reset_password",
            new { loginName, reason },
            async _ =>
            {
                ValidateReason(reason);
                if (password is not { Length: >= 3 and <= 20 } || password.Any(c => c is < ' ' or > '~'))
                {
                    throw new AdminOperationException($"The {nameof(password)} must contain 3-20 printable ASCII characters.", AdminOperationErrorKind.InvalidInput);
                }

                var config = await configurations.GetOwnerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                using var check = persistence.CreateNewPlayerContext(config);
                var checkedAccount = await check.GetAccountByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound(loginName);
                await this.RequireOfflineAsync(checkedAccount.LoginName).ConfigureAwait(false);
                if (IsPrivileged(checkedAccount.State))
                {
                    await executor.RequireAsync(AdminRoles.Administrator, cancellationToken).ConfigureAwait(false);
                }

                return await this.ChangeOfflineAccountAsync(
                    checkedAccount.LoginName,
                    config,
                    async (account, context) =>
                    {
                        if (IsPrivileged(account.State))
                        {
                            await executor.RequireAsync(AdminRoles.Administrator, cancellationToken).ConfigureAwait(false);
                        }

                        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                        await SaveAsync(context, cancellationToken).ConfigureAwait(false);
                        return AdminReadResult.From(new { loginName = account.LoginName, status = "password_reset" });
                    },
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Validates that an operational audit reason is bounded and nonempty.</summary>
    /// <param name="reason">The reason.</param>
    internal static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
        {
            throw new AdminOperationException($"An audit {nameof(reason)} of 1-1000 characters is required.", AdminOperationErrorKind.InvalidInput);
        }
    }

    /// <summary>Tells the caller whether the account change itself succeeded when its reservation stayed behind.</summary>
    /// <param name="operationFailure">The failure of the change, or <c>null</c> when it completed.</param>
    /// <returns>The safe message for the uncertain outcome.</returns>
    internal static string ReleaseFailureMessage(Exception? operationFailure)
        => operationFailure is null
            ? "The account change finished, but releasing its login reservation could not be confirmed. Check the current state and login coordination before retrying."
            : "The account change failed, and releasing its login reservation could not be confirmed either. Inspect the account before retrying.";

    private static bool IsPrivileged(AccountState state) => state is not (AccountState.Normal or AccountState.Banned or AccountState.TemporarilyBanned);

    private static AdminResourceNotFoundException NotFound(string loginName)
        => new("Account not found. Use accounts_search to find its exact login name.", nameof(loginName));

    private static async ValueTask SaveAsync(IContext context, CancellationToken cancellationToken)
    {
        if (!await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AdminOperationException("The account change could not be saved.");
        }
    }

    /// <summary>
    /// Changes an account under an exclusive login-server reservation, so a login racing the change cannot make
    /// it operate on stale online state. The account is loaded again after the reservation was acquired.
    /// </summary>
    /// <param name="loginName">The exact account login name.</param>
    /// <param name="config">The active game configuration.</param>
    /// <param name="change">The change, executed inside the reservation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The safe result of the change.</returns>
    private async ValueTask<JsonObject> ChangeOfflineAccountAsync(string loginName, GameConfiguration config, Func<Account, IPlayerContext, ValueTask<JsonObject>> change, CancellationToken cancellationToken)
    {
        var reservationId = RandomNumberGenerator.GetInt32(byte.MaxValue + 1, int.MaxValue);
        if (!await login.TryReserveAccountAsync(loginName, reservationId).ConfigureAwait(false))
        {
            throw new AdminOperationException("The account is online, reserved, or its login state is unavailable. Disconnect it first; in distributed deployments, log in and out once to initialize a new account's login state.");
        }

        JsonObject? result = null;
        Exception? operationFailure = null;
        try
        {
            // Reload only after the reservation: an online session may have saved just before it was acquired.
            using var context = persistence.CreateNewPlayerContext(config);
            var account = await context.GetAccountByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false)
                ?? throw new AdminOperationException("The account no longer exists.");
            result = await change(account, context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            // Also release after a lost reserve response; ownership checking makes this safe for online accounts.
            await login.ReleaseAccountReservationAsync(loginName, reservationId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new AdminOperationException(
                ReleaseFailureMessage(operationFailure),
                AdminOperationErrorKind.Uncertain,
                operationFailure is null ? exception : new AggregateException(operationFailure, exception));
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return result ?? throw new InvalidOperationException("The account operation produced no result.");
    }

    private async ValueTask RequireOfflineAsync(string loginName)
    {
        if ((await login.GetSnapshotAsync().ConfigureAwait(false)).Keys.Contains(loginName, StringComparer.OrdinalIgnoreCase))
        {
            throw new AdminOperationException("The account is online. Use players_disconnect first, or players_ban to ban a live character.");
        }
    }
}
