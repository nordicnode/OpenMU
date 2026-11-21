// <copyright file="PersistenceFailures.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

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
    /// <summary>The exception type names which mean that a save was rejected by the store.</summary>
    private static readonly string[] RejectedSaveTypes =
    [
        "DbUpdateException",
        "DbUpdateConcurrencyException",
        "PostgresException",
        "MySqlException",
        "SqliteException",
        "SqlException",
    ];

    /// <summary>The exception type names which mean that the store or its transport failed.</summary>
    private static readonly string[] UnavailableTypes =
    [
        "NpgsqlException",
        "RedisConnectionException",
        "DaprException",
    ];

    /// <summary>Determines whether the failure is a rejected save, for example a duplicate unique value.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c>, if the store rejected the change instead of accepting it.</returns>
    internal static bool IsRejectedSave(Exception exception)
        => Walk(exception).Any(error => error is PersistenceConflictException || RejectedSaveTypes.Contains(error.GetType().Name, StringComparer.Ordinal));

    /// <summary>Determines whether the failure originates from the persistence store.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c>, if a database or its provider rejected or interrupted the operation.</returns>
    internal static bool IsDatabaseFailure(Exception exception)
        => Walk(exception).Any(error => error is PersistenceConflictException
            || RejectedSaveTypes.Contains(error.GetType().Name, StringComparer.Ordinal)
            || UnavailableTypes.Contains(error.GetType().Name, StringComparer.Ordinal)
            || error.GetType().Name.EndsWith("DbException", StringComparison.Ordinal));

    private static IEnumerable<Exception> Walk(Exception exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            yield return error;
        }
    }
}
