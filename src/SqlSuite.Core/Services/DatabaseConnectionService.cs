using Microsoft.Data.SqlClient;

namespace SqlSuite.Core.Services;

/// <summary>A database exposed by a server, used to populate the database pickers.</summary>
public sealed record DatabaseInfo(string Name, string? State, DateTime? CreateDate)
{
    public override string ToString() => Name;
}

/// <summary>Small helper around <see cref="SqlConnection"/> for the connection screen.</summary>
public sealed class DatabaseConnectionService
{
    /// <summary>Opens a connection and runs <c>SELECT @@VERSION</c>. Throws when the server is unreachable.</summary>
    public async Task<string> TestConnectionAsync(Models.DatabaseConnectionInfo info, string? databaseName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        await using SqlConnection connection = new(
            string.IsNullOrWhiteSpace(databaseName) ? info.GetConnectionString() : info.GetConnectionString(databaseName));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = new("SELECT @@VERSION;", connection);
        object? version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version as string ?? string.Empty;
    }

    /// <summary>Lists the online databases of the server the supplied connection points at.</summary>
    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(Models.DatabaseConnectionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        await using SqlConnection connection = new(info.GetMasterConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT name, state_desc, create_date
            FROM sys.databases
            WHERE database_id > 4
            ORDER BY name;
            """;

        await using SqlCommand command = new(sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<DatabaseInfo> databases = new();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            databases.Add(new DatabaseInfo(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
        }

        return databases;
    }
}
