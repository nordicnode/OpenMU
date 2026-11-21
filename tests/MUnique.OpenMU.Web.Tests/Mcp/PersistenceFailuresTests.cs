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
    }

    /// <summary>A wrapped driver error is found through the inner exception chain.</summary>
    [Test]
    public void WalksTheInnerExceptionChain()
    {
        var exception = new InvalidOperationException("outer", new NpgsqlException("connection refused"));
        Assert.That(PersistenceFailures.IsDatabaseFailure(exception), Is.True);
    }

    /// <summary>Unrelated failures are not classified as persistence failures.</summary>
    [Test]
    public void LeavesOtherFailuresAlone()
    {
        Assert.That(PersistenceFailures.IsDatabaseFailure(new ArgumentException("bad argument")), Is.False);
        Assert.That(PersistenceFailures.IsRejectedSave(new ArgumentException("bad argument")), Is.False);
        Assert.That(PersistenceFailures.IsDatabaseFailure(new InvalidOperationException("not a database")), Is.False);
    }

    /// <summary>Stand-in for the Npgsql driver exception, which the provider-neutral layer only knows by name.</summary>
    private sealed class NpgsqlException(string message) : Exception(message);
}
