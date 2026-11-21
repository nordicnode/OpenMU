// <copyright file="AdminToolExecution.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Mcp;

using System.Text.Json.Nodes;
using ModelContextProtocol;
using MUnique.OpenMU.Web.AdminPanel.Operations;

/// <summary>
/// Turns operation failures into safe tool errors. Nothing but the curated messages below reaches the client;
/// stack traces, SQL and internal type names stay in the host log.
/// </summary>
internal static class AdminToolExecution
{
    /// <summary>Executes the shared helper with the supplied values.</summary>
    /// <param name="operation">The operation delegate.</param>
    /// <returns>The safe result.</returns>
    internal static async ValueTask<JsonObject> ExecuteAsync(Func<ValueTask<JsonObject>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or AdminOperationException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex) when (PersistenceFailures.IsDatabaseFailure(ex))
        {
            throw new McpException("The database rejected the operation or its outcome is unknown. The data may still be referenced or was changed concurrently; refresh the state and request a new preview.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unexpected failure must not leak provider or implementation details to the client. The inner
            // exception stays attached for the host log.
            throw new McpException("The operation failed unexpectedly. Check the admin panel log before retrying.", ex);
        }
    }
}
