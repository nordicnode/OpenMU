// <copyright file="PersistenceFailuresTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>
/// Verifies the provider-neutral classifier which decides whether an operation failed because the store rejected
/// the change - so nothing was written - or because the store or its transport broke down.
/// </summary>
[TestFixture]
public sealed class PersistenceFailuresTests
{
    /// <summary>A translated save conflict is recognized as a verified rejection.</summary>
    [Test]
    public void RecognizesTranslatedConflicts()
    {
        var exception = new PersistenceConflictException(new DbUpdateException("duplicate unique value"));
        Assert.That(PersistenceFailures.IsRejectedSave(exception), Is.True);
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
    }

    /// <summary>A provider wrapper without a recognized driver error is a database failure, but no verified rejection.</summary>
    [Test]
    public void DistinguishesWrappersFromRejections()
    {
        var exception = new DbUpdateException("the store could not be reached");
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
        Assert.That(PersistenceFailures.IsRejectedSave(exception), Is.True, "A DbUpdateException itself means the save did not succeed.");
    }

    /// <summary>A driver connectivity error is a database failure without a rejection.</summary>
    [Test]
    public void RecognizesTransportFailures()
    {
        var exception = new NpgsqlException("connection refused");
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
        Assert.That(PersistenceFailures.IsRejectedSave(exception), Is.False);
        Assert.That(PersistenceFailures.IsRejectedSave(new DbUpdateException("save failed", exception)), Is.False, "A save which lost its connection is not a clean rejection.");
    }

    /// <summary>The SQLSTATE class of a provider error decides whether the save was rejected or interrupted.</summary>
    /// <param name="sqlState">The SQLSTATE of the server error.</param>
    /// <param name="rejected">Whether the save was rejected.</param>
    [TestCase("23505", true)]
    [TestCase("23503", true)]
    [TestCase("40001", false)]
    [TestCase("40P01", false)]
    [TestCase("57P01", false)]
    [TestCase("08006", false)]
    public void ClassifiesProviderErrors(string sqlState, bool rejected)
    {
        var exception = new DbUpdateException("save failed", new PostgresException(sqlState));
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
        Assert.That(PersistenceFailures.IsRejectedSave(exception), Is.EqualTo(rejected));
    }

    /// <summary>A wrapped driver error is found through the inner exception chain.</summary>
    [Test]
    public void WalksTheInnerExceptionChain()
    {
        var exception = new InvalidOperationException("outer", new NpgsqlException("connection refused"));
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
    }

    /// <summary>Secondary exceptions of an aggregate are classified too.</summary>
    [Test]
    public void WalksAggregateExceptions()
    {
        var exception = new AggregateException(new ArgumentException("unrelated"), new DbUpdateException("duplicate unique value"));
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
        Assert.That(PersistenceFailures.IsRejectedSave(exception), Is.True);

        var interrupted = new AggregateException(new DbUpdateException("save failed", new NpgsqlException("connection refused")));
        Assert.That(PersistenceFailures.IsDatabaseFailure(interrupted), Is.True);
        Assert.That(PersistenceFailures.IsRejectedSave(interrupted), Is.False);
    }

    /// <summary>Unrelated failures are not classified as persistence failures.</summary>
    [Test]
    public void LeavesOtherFailuresAlone()
    {
        Assert.That(PersistenceFailures.IsDatabaseFailure(new ArgumentException("bad argument")), Is.False);
        Assert.That(PersistenceFailures.IsRejectedSave(new ArgumentException("bad argument")), Is.False);
        Assert.That(PersistenceFailures.IsDatabaseFailure(new InvalidOperationException("not a database")), Is.False);
    }

    /// <summary>Stand-in for the Npgsql driver's connection level exception, known by name to the provider-neutral layer.</summary>
    private sealed class NpgsqlException(string message) : Exception(message);

    /// <summary>Stand-in for the Npgsql server error, whose SQLSTATE decides between rejection and interruption.</summary>
    private sealed class PostgresException(string sqlState) : Exception("server error")
    {
        /// <summary>Gets the SQLSTATE of the server error.</summary>
        public string SqlState { get; } = sqlState;
    }
}
