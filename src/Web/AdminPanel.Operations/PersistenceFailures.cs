// <copyright file="PersistenceFailures.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Reflection;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Recognizes persistence failures at the operation boundary.
/// </summary>
/// <remarks>
/// The admin operations layer only references the provider-neutral <see cref="IContext"/> abstraction, so it
/// cannot catch a database provider's exception type directly. The save paths already translate what they can
/// see (see <see cref="PersistenceConflictException"/>); this classifier covers the remaining wrappers by type
/// name, which is enough to answer "was the change rejected by the store" without depending on Entity Framework
/// or a specific database driver.
/// </remarks>
internal static class PersistenceFailures
{
    /// <summary>The exception type names which wrap a save which did not complete.</summary>
    private static readonly string[] SaveFailureTypes =
    [
        "DbUpdateException",
        "DbUpdateConcurrencyException",
    ];

    /// <summary>The exception type names of provider errors raised by the database itself.</summary>
    private static readonly string[] ProviderErrorTypes =
    [
        "PostgresException",
        "MySqlException",
        "SqliteException",
        "SqlException",
    ];

    /// <summary>The exception type names which mean that the store or its transport failed.</summary>
    private static readonly string[] TransportTypes =
    [
        "NpgsqlException",
        "SocketException",
        "TimeoutException",
        "IOException",
        "RedisException",
        "RedisConnectionException",
        "DaprException",
    ];

    /// <summary>
    /// The SQLSTATE classes which do not indicate a rejected value: connection loss, transaction rollback
    /// (serialization failure, deadlock), resource shortage, operator intervention and system errors.
    /// </summary>
    private static readonly string[] TransportSqlStates = ["08", "40", "53", "57", "58"];

    /// <summary>Determines whether the failure is a rejected save, for example a duplicate unique value.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c>, if the store rejected the change instead of accepting it.</returns>
    internal static bool IsRejectedSave(Exception exception)
    {
        var chain = Walk(exception).ToList();
        if (chain.Any(error => error is PersistenceConflictException))
        {
            return true;
        }

        // A lost connection, a timeout or a deadlock is not a clean rejection: the outcome is unknown.
        return !chain.Any(IsTransportFailure)
            && chain.Any(error => SaveFailureTypes.Contains(error.GetType().Name, StringComparer.Ordinal));
    }

    /// <summary>Determines whether the failure originates from the persistence store.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c>, if a database or its provider rejected or interrupted the operation.</returns>
    internal static bool IsDatabaseFailure(Exception exception)
        => Walk(exception).Any(error => error is PersistenceConflictException
            || SaveFailureTypes.Contains(error.GetType().Name, StringComparer.Ordinal)
            || ProviderErrorTypes.Contains(error.GetType().Name, StringComparer.Ordinal)
            || TransportTypes.Contains(error.GetType().Name, StringComparer.Ordinal)
            || IsTransportFailure(error)
            || error.GetType().Name.EndsWith("DbException", StringComparison.Ordinal));

    private static bool IsTransportFailure(Exception exception)
    {
        if (TransportTypes.Contains(exception.GetType().Name, StringComparer.Ordinal))
        {
            return true;
        }

        // Providers expose the SQLSTATE as a string property; its class decides whether a retry can succeed.
        if (exception.GetType().GetProperty("SqlState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exception) is string sqlState)
        {
            return TransportSqlStates.Any(sqlState.StartsWith);
        }

        return false;
    }

    private static IEnumerable<Exception> Walk(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
