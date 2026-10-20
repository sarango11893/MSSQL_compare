using Microsoft.Data.SqlClient;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

/// <summary>Outcome of applying a T-SQL script to a database.</summary>
/// <param name="Succeeded">True when every batch ran without error.</param>
/// <param name="ExecutedBatches">Number of batches that completed.</param>
/// <param name="TotalBatches">Number of batches the script was split into.</param>
/// <param name="FailedBatchIndex">0-based index of the batch that failed, or -1.</param>
/// <param name="FailedBatch">Text of the batch that failed, when one did.</param>
/// <param name="Error">The error message reported by SQL Server.</param>
/// <param name="Duration">Wall-clock time spent executing.</param>
public sealed record ScriptExecutionResult(
    bool Succeeded,
    int ExecutedBatches,
    int TotalBatches,
    int FailedBatchIndex,
    string FailedBatch,
    string Error,
    TimeSpan Duration)
{
    public static ScriptExecutionResult Success(int executed, TimeSpan duration) =>
        new(true, executed, executed, -1, string.Empty, string.Empty, duration);

    public static ScriptExecutionResult Failure(
        int executed,
        int total,
        int failedIndex,
        string failedBatch,
        string error,
        TimeSpan duration) =>
        new(false, executed, total, failedIndex, failedBatch, error, duration);

    public string Summary => Succeeded
        ? $"Script aplicado correctamente ({ExecutedBatches} lote(s))."
        : $"Error en el lote {FailedBatchIndex + 1} de {TotalBatches}: {Error}";
}

/// <summary>
/// Executes comparison scripts against a database. Both the schema (DDL) script produced by DacFx
/// and the data (DML) script produced by <see cref="DataScriptGenerator"/> contain <c>GO</c>
/// separators, so they are executed batch by batch inside a single connection.
/// </summary>
public sealed class ScriptExecutionService
{
    private const int DefaultCommandTimeoutSeconds = 180;

    /// <summary>
    /// Normalizes the script (resolving <c>sqlcmd</c> variables when needed), splits it on
    /// <c>GO</c> and executes every batch sequentially.
    /// </summary>
    /// <param name="connection">The database the script is applied to.</param>
    /// <param name="script">The script to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ScriptExecutionResult> ExecuteAsync(
        DatabaseConnectionInfo connection,
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(script))
            return ScriptExecutionResult.Success(0, TimeSpan.Zero);

        NormalizedScript normalized = SqlCmdScriptProcessor.Normalize(script, connection.DatabaseName);
        IReadOnlyList<string> batches = SqlCmdScriptProcessor.SplitBatches(normalized.Script);

        if (batches.Count == 0)
            return ScriptExecutionResult.Success(0, TimeSpan.Zero);

        var startedAt = DateTime.UtcNow;
        var executed = 0;

        await using var sqlConnection = new SqlConnection(connection.GetConnectionString());
        await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < batches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = sqlConnection.CreateCommand();
            command.CommandText = batches[index];
            command.CommandTimeout = DefaultCommandTimeoutSeconds;

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                executed++;
            }
            catch (SqlException ex)
            {
                return ScriptExecutionResult.Failure(
                    executed,
                    batches.Count,
                    index,
                    batches[index],
                    ex.Message,
                    DateTime.UtcNow - startedAt);
            }
        }

        return ScriptExecutionResult.Success(executed, DateTime.UtcNow - startedAt);
    }

    /// <summary>Synchronous wrapper around <see cref="ExecuteAsync"/>.</summary>
    public ScriptExecutionResult Execute(
        DatabaseConnectionInfo connection,
        string script,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(connection, script, cancellationToken).GetAwaiter().GetResult();
}
