using Microsoft.Data.SqlClient;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

internal static class SqlTableMetadataReader
{
    public static async Task<IReadOnlyList<TableSchemaInfo>> ReadTablesAsync(
        SqlConnection connection,
        IReadOnlyList<string> includedTables,
        IList<TableKeyMapping> comparisonKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(includedTables);
        ArgumentNullException.ThrowIfNull(comparisonKeys);

        var tableFilter = BuildTableFilter("s", includedTables, out var parameters);
        const string columnsSql = """
            SELECT s.name, t.name, c.name, TYPE_NAME(c.user_type_id),
                   c.max_length, c.precision, c.scale, c.is_nullable,
                   c.is_identity, c.is_computed
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
              AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
            """;

        using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = columnsSql + tableFilter + " ORDER BY s.name, t.name, c.column_id;";
        AddParameters(columnsCommand, parameters);

        var tables = new Dictionary<string, TableBuilder>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schemaName = reader.GetString(0);
                var tableName = reader.GetString(1);
                var fullName = $"{schemaName}.{tableName}";
                if (!tables.TryGetValue(fullName, out var table))
                {
                    table = new TableBuilder(schemaName, tableName);
                    tables.Add(fullName, table);
                }

                var dataTypeName = reader.GetString(3);
                table.Columns.Add(new ColumnSchemaInfo
                {
                    Name = reader.GetString(2),
                    DataTypeName = dataTypeName,
                    MaxLength = reader.GetInt16(4),
                    Precision = reader.GetByte(5),
                    Scale = reader.GetByte(6),
                    IsNullable = reader.GetBoolean(7),
                    IsIdentity = reader.GetBoolean(8),
                    IsComputed = reader.GetBoolean(9),
                    IsRowVersion = dataTypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase)
                        || dataTypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        if (tables.Count == 0)
            return Array.Empty<TableSchemaInfo>();

        var keyFilter = BuildTableFilter("s", includedTables, out parameters);
        const string keysSql = """
            WITH KeyCandidates AS
            (
                SELECT i.object_id, i.index_id,
                       CASE WHEN i.is_primary_key = 1 THEN N'Primary key'
                            WHEN i.is_unique_constraint = 1 THEN N'Unique constraint'
                            ELSE N'Unique index' END AS KeySource,
                       COUNT_BIG(*) AS KeyColumnCount
                FROM sys.indexes AS i
                INNER JOIN sys.tables AS t ON t.object_id = i.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                INNER JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id
                   AND ic.index_id = i.index_id
                   AND ic.key_ordinal > 0
                WHERE t.is_ms_shipped = 0
                  AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
                  /*TABLE_FILTER*/
                  AND i.is_unique = 1
                  AND i.is_disabled = 0
                  AND i.is_hypothetical = 0
                  AND i.has_filter = 0
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM sys.index_columns AS key_ic
                      INNER JOIN sys.columns AS key_c
                          ON key_c.object_id = key_ic.object_id
                         AND key_c.column_id = key_ic.column_id
                      WHERE key_ic.object_id = i.object_id
                        AND key_ic.index_id = i.index_id
                        AND key_ic.key_ordinal > 0
                        AND (key_c.is_nullable = 1 OR key_c.is_computed = 1)
                  )
                GROUP BY i.object_id, i.index_id, i.is_primary_key, i.is_unique_constraint
            ),
            RankedKeys AS
            (
                SELECT object_id, index_id, KeySource,
                       ROW_NUMBER() OVER
                       (
                           PARTITION BY object_id
                           ORDER BY CASE KeySource
                                        WHEN N'Primary key' THEN 1
                                        WHEN N'Unique constraint' THEN 2
                                        ELSE 3
                                    END,
                                    KeyColumnCount,
                                    index_id
                       ) AS key_rank
                FROM KeyCandidates
            )
            SELECT s.name, t.name, rk.KeySource, ic.key_ordinal, c.name, rk.key_rank, rk.index_id
            FROM RankedKeys AS rk
            INNER JOIN sys.tables AS t ON t.object_id = rk.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = rk.object_id
               AND ic.index_id = rk.index_id
               AND ic.key_ordinal > 0
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id
               AND c.column_id = ic.column_id
            """;

        using var keysCommand = connection.CreateCommand();
        keysCommand.CommandText = keysSql.Replace("/*TABLE_FILTER*/", keyFilter, StringComparison.Ordinal)
            + " ORDER BY s.name, t.name, rk.key_rank, ic.key_ordinal;";
        AddParameters(keysCommand, parameters);

        await using (var reader = await keysCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var fullName = $"{reader.GetString(0)}.{reader.GetString(1)}";
                if (!tables.TryGetValue(fullName, out var table))
                    continue;

                var indexId = reader.GetInt32(6);
                if (!table.KeyCandidates.TryGetValue(indexId, out var candidate))
                {
                    candidate = new KeyCandidateBuilder(reader.GetInt64(5), reader.GetString(2));
                    table.KeyCandidates.Add(indexId, candidate);
                }

                candidate.Columns.Add(reader.GetString(4));
            }
        }

        foreach (var table in tables.Values)
        {
            var mapping = comparisonKeys.FirstOrDefault(candidate =>
                candidate.Table.Equals(table.FullName, StringComparison.OrdinalIgnoreCase));
            if (mapping is null)
            {
                var defaultKey = table.KeyCandidates.Values
                    .OrderBy(candidate => candidate.Rank)
                    .FirstOrDefault();
                if (defaultKey is not null)
                {
                    table.KeySource = defaultKey.Source;
                    table.KeyColumns.AddRange(defaultKey.Columns);
                }

                continue;
            }

            var configuredColumns = mapping.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .Select(column => column.Trim())
                .ToList();
            var configuredKey = table.KeyCandidates.Values.FirstOrDefault(candidate =>
                candidate.Columns.SequenceEqual(configuredColumns, StringComparer.OrdinalIgnoreCase));
            if (configuredColumns.Count == 0 || configuredKey is null)
            {
                table.KeySelectionError =
                    $"La clave configurada ({string.Join(", ", configuredColumns)}) no coincide con una clave única no nula de la tabla.";
                continue;
            }

            table.KeySource = $"Clave de comparación configurada ({configuredKey.Source})";
            table.KeyColumns.AddRange(configuredKey.Columns);
        }

        return tables.Values.Select(table => new TableSchemaInfo
        {
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            Columns = table.Columns,
            KeyColumns = table.KeyColumns,
            KeySource = table.KeySource,
            KeySelectionError = table.KeySelectionError,
        }).ToList();
    }

    internal static string BuildTableFilter(
        string schemaAlias,
        IReadOnlyList<string> includedTables,
        out IReadOnlyList<(string Name, string Value)> parameters)
    {
        var conditions = new List<string>();
        var values = new List<(string Name, string Value)>();

        for (var index = 0; index < includedTables.Count; index++)
        {
            var normalized = includedTables[index]
                .Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal)
                .Trim();
            var separator = normalized.IndexOf('.');
            var schemaParameter = $"@schema{index}";
            var tableParameter = $"@table{index}";

            if (separator >= 0)
            {
                conditions.Add($"({schemaAlias}.name COLLATE Latin1_General_100_CI_AS = {schemaParameter} "
                    + $"COLLATE Latin1_General_100_CI_AS AND t.name COLLATE Latin1_General_100_CI_AS = {tableParameter} "
                    + "COLLATE Latin1_General_100_CI_AS)");
                values.Add((schemaParameter, normalized[..separator]));
                values.Add((tableParameter, normalized[(separator + 1)..]));
            }
            else
            {
                conditions.Add($"(t.name COLLATE Latin1_General_100_CI_AS = {tableParameter} "
                    + "COLLATE Latin1_General_100_CI_AS)");
                values.Add((tableParameter, normalized));
            }
        }

        parameters = values;
        return conditions.Count == 0
            ? string.Empty
            : $" AND ({string.Join(" OR ", conditions)})";
    }

    private static void AddParameters(
        SqlCommand command,
        IReadOnlyList<(string Name, string Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
    }

    private sealed class TableBuilder(string schemaName, string tableName)
    {
        public string SchemaName { get; } = schemaName;
        public string TableName { get; } = tableName;
        public string FullName => $"{SchemaName}.{TableName}";
        public List<ColumnSchemaInfo> Columns { get; } = [];
        public List<string> KeyColumns { get; } = [];
        public string KeySource { get; set; } = string.Empty;
        public string? KeySelectionError { get; set; }
        public Dictionary<int, KeyCandidateBuilder> KeyCandidates { get; } = [];
    }

    private sealed class KeyCandidateBuilder(long rank, string source)
    {
        public long Rank { get; } = rank;
        public string Source { get; } = source;
        public List<string> Columns { get; } = [];
    }
}
