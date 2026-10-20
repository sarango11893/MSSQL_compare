using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSuite.Core.Enums;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

/// <summary>
/// Module B: row-level data comparison (DML).
/// </summary>
/// <remarks>
/// Both sides are streamed with a forward-only <see cref="SqlDataReader"/> ordered by the
/// table key, then merged. Nothing is buffered except the rows that actually differ.
/// </remarks>
public sealed class DataComparerService
{
    private readonly ComparisonOptions _defaultOptions;

    public DataComparerService(ComparisonOptions? defaultOptions = null)
        => _defaultOptions = defaultOptions ?? new ComparisonOptions();

    /// <summary>Compares the data of two databases.</summary>
    public DataComparisonResultInfo Compare(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var effective = (options ?? _defaultOptions).Clone();
        var startedAt = DateTime.UtcNow;
        var errors = new List<string>();

        TSqlModel sourceModel;
        TSqlModel targetModel;

        try
        {
            sourceModel = LoadModel(source);
            targetModel = LoadModel(target);
        }
        catch (Exception ex)
        {
            return new DataComparisonResultInfo
            {
                IsValid = false,
                Errors = new[] { $"No se pudo leer el esquema de origen o destino: {ex.Message}" },
                Duration = DateTime.UtcNow - startedAt,
            };
        }

        var sourceTables = DacModelReader.ReadTables(sourceModel)
            .ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
        var targetTables = DacModelReader.ReadTables(targetModel)
            .ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableDataDifference>();
        var skipped = new List<string>();

        using var sourceConnection = new SqlConnection(source.GetConnectionString());
        using var targetConnection = new SqlConnection(target.GetConnectionString());

        try
        {
            sourceConnection.Open();
            targetConnection.Open();
        }
        catch (Exception ex)
        {
            return new DataComparisonResultInfo
            {
                IsValid = false,
                Errors = new[] { $"No se pudo conectar con origen o destino: {ex.Message}" },
                Duration = DateTime.UtcNow - startedAt,
            };
        }

        foreach (var (fullName, sourceTable) in sourceTables.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExcluded(fullName, effective))
            {
                skipped.Add($"{fullName} (excluida por configuración)");
                continue;
            }

            if (!targetTables.TryGetValue(fullName, out var targetTable))
            {
                skipped.Add($"{fullName} (no existe en destino)");
                continue;
            }

            if (!sourceTable.KeyColumns.SequenceEqual(targetTable.KeyColumns, StringComparer.OrdinalIgnoreCase))
            {
                skipped.Add($"{fullName} (clave de fila distinta entre origen y destino)");
                continue;
            }

            if (!sourceTable.HasKey)
            {
                skipped.Add($"{fullName} (sin clave primaria ni índice único utilizable)");
                continue;
            }

            var comparableColumns = BuildComparableColumns(sourceTable, targetTable, effective);
            if (comparableColumns.Count == 0)
            {
                skipped.Add($"{fullName} (sin columnas comparables)");
                continue;
            }

            try
            {
                var difference = CompareTable(
                    sourceConnection,
                    targetConnection,
                    sourceTable,
                    comparableColumns,
                    effective,
                    cancellationToken);

                tables.Add(difference);
            }
            catch (Exception ex)
            {
                errors.Add($"{fullName}: {ex.Message}");
            }
        }

        var warnings = tables.SelectMany(t => t.Warnings).ToList();
        var allEmpty = tables.All(t => t.TotalCount == 0);

        var script = effective.GenerateDeploymentScript && !allEmpty
            ? new DataScriptGenerator(effective).Generate(tables)
            : string.Empty;

        return new DataComparisonResultInfo
        {
            IsValid = errors.Count == 0,
            // A skipped table is not a proven match, so it must not be reported as equality.
            IsEqual = allEmpty && errors.Count == 0 && skipped.Count == 0,
            Script = script,
            Tables = tables,
            SkippedTables = skipped,
            Errors = errors,
            Warnings = warnings,
            Duration = DateTime.UtcNow - startedAt,
        };
    }

    /// <summary>Enumerates the tables (with keys) that a data comparison would process.</summary>
    public IReadOnlyList<TableSchemaInfo> DiscoverTables(DatabaseConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return DacModelReader.ReadTables(LoadModel(connection)).ToList();
    }

    private static TSqlModel LoadModel(DatabaseConnectionInfo connection)
        => TSqlModel.LoadFromDatabase(
            connection.GetConnectionString(),
            new ModelExtractOptions { LoadAsScriptBackedModel = false });

    private static bool IsExcluded(string fullName, ComparisonOptions options)
        => options.ExcludedTables.Any(e => e.Equals(fullName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Columns present on both sides that a script could write to. When
    /// <see cref="ComparisonOptions.SkipNonWritableColumns"/> is disabled computed and
    /// rowversion columns are read too, but never written.
    /// </summary>
    private static IReadOnlyList<ColumnSchemaInfo> BuildComparableColumns(
        TableSchemaInfo source,
        TableSchemaInfo target,
        ComparisonOptions options)
    {
        var result = new List<ColumnSchemaInfo>();

        foreach (var column in source.Columns)
        {
            var counterpart = target.FindColumn(column.Name);
            if (counterpart is null)
                continue;

            if (options.SkipNonWritableColumns && !column.IsWritable)
                continue;

            result.Add(column);
        }

        return result;
    }

    private static TableDataDifference CompareTable(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchemaInfo sourceTable,
        IReadOnlyList<ColumnSchemaInfo> columns,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        var changes = new List<RowChange>();
        var truncated = false;

        using var sourceReader = ExecuteQuery(sourceConnection, sourceTable, columns, options);
        using var targetReader = ExecuteQuery(targetConnection, sourceTable, columns, options);
        var sourceRow = new RowBuffer(sourceReader, columns);
        var targetRow = new RowBuffer(targetReader, columns);

        var hasSource = sourceRow.Read();
        var hasTarget = targetRow.Read();

        while (hasSource || hasTarget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!hasSource)
            {
                changes.Add(BuildChange(RowChangeKind.Delete, sourceTable, null, targetRow, columns, options));
                hasTarget = targetRow.Read();
                continue;
            }

            if (!hasTarget)
            {
                changes.Add(BuildChange(RowChangeKind.Insert, sourceTable, sourceRow, null, columns, options));
                hasSource = sourceRow.Read();
                continue;
            }

            var comparison = CompareKeys(sourceRow, targetRow, sourceTable.KeyColumns, options);

            if (comparison < 0)
            {
                changes.Add(BuildChange(RowChangeKind.Insert, sourceTable, sourceRow, null, columns, options));
                hasSource = sourceRow.Read();
            }
            else if (comparison > 0)
            {
                changes.Add(BuildChange(RowChangeKind.Delete, sourceTable, null, targetRow, columns, options));
                hasTarget = targetRow.Read();
            }
            else
            {
                var changed = FindChangedColumns(sourceRow, targetRow, columns, options);
                if (changed.Count > 0)
                {
                    changes.Add(BuildChange(RowChangeKind.Update, sourceTable, sourceRow, targetRow, columns, options, changed));
                }

                hasSource = sourceRow.Read();
                hasTarget = targetRow.Read();
            }

            if (changes.Count >= options.MaxRowsPerTable)
            {
                truncated = true;
                break;
            }
        }

        return new TableDataDifference
        {
            Table = sourceTable,
            Changes = changes,
            Warnings = truncated
                ? new[] { $"Se alcanzó el límite de {options.MaxRowsPerTable} filas; el resultado está truncado." }
                : Array.Empty<string>(),
        };
    }

    private static SqlDataReader ExecuteQuery(
        SqlConnection connection,
        TableSchemaInfo table,
        IReadOnlyList<ColumnSchemaInfo> columns,
        ComparisonOptions options)
    {
        // The key must be part of the projection even if it is not comparable (computed key columns
        // are rejected earlier, so this is only about the SELECT list order).
        var selected = new List<string>();
        foreach (var key in table.KeyColumns)
        {
            if (!selected.Contains(key, StringComparer.OrdinalIgnoreCase))
                selected.Add(key);
        }

        foreach (var column in columns)
        {
            if (!selected.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
                selected.Add(column.Name);
        }

        var sql = new StringBuilder();
        sql.Append("SELECT ");
        sql.Append(string.Join(", ", selected.Select(SqlLiteralFormatter.QuoteIdentifier)));
        sql.Append(" FROM ");
        sql.Append(table.QualifiedName);
        sql.Append(" ORDER BY ");
        sql.Append(string.Join(", ", table.KeyColumns.Select(SqlLiteralFormatter.QuoteIdentifier)));

        var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        return command.ExecuteReader(CommandBehavior.Default);
    }

    private static int CompareKeys(RowBuffer source, RowBuffer target, IReadOnlyList<string> keyColumns, ComparisonOptions options)
    {
        foreach (var key in keyColumns)
        {
            var left = source[key];
            var right = target[key];
            var result = CompareValues(left, right, options);
            if (result != 0)
                return result;
        }

        return 0;
    }

    private static IReadOnlyList<ColumnValueChange> FindChangedColumns(
        RowBuffer source,
        RowBuffer target,
        IReadOnlyList<ColumnSchemaInfo> columns,
        ComparisonOptions options)
    {
        var changed = new List<ColumnValueChange>();

        foreach (var column in columns)
        {
            var left = source[column.Name];
            var right = target[column.Name];

            if (CompareValues(left, right, options) != 0)
                changed.Add(new ColumnValueChange(column.Name, left, right));
        }

        return changed;
    }

    /// <summary>Compares two values, normalising strings when a case-insensitive comparison is requested.</summary>
    internal static int CompareValues(object? left, object? right, ComparisonOptions options)
    {
        var leftNull = left is null or DBNull;
        var rightNull = right is null or DBNull;

        if (leftNull)
            return rightNull ? 0 : -1;

        if (rightNull)
            return 1;

        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return CompareBytes(leftBytes, rightBytes);

        if (IsNumeric(left) && IsNumeric(right))
        {
            var leftDecimal = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            var rightDecimal = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return leftDecimal.CompareTo(rightDecimal);
        }

        if (left is string leftString && right is string rightString)
        {
            return string.Compare(
                leftString,
                rightString,
                options.CaseSensitiveDataComparison ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        if (!leftNull && right is not null && left is IComparable comparable && left.GetType() == right.GetType())
            return comparable.CompareTo(right);

        return string.CompareOrdinal(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture));
    }

    private static bool IsNumeric(object? value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    private static int CompareBytes(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);

        for (var i = 0; i < length; i++)
        {
            var result = left[i].CompareTo(right[i]);
            if (result != 0)
                return result;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static RowChange BuildChange(
        RowChangeKind kind,
        TableSchemaInfo table,
        RowBuffer? source,
        RowBuffer? target,
        IReadOnlyList<ColumnSchemaInfo> columns,
        ComparisonOptions options,
        IReadOnlyList<ColumnValueChange>? changed = null)
    {
        var primary = source ?? target!;
        var keyValues = table.KeyColumns.Select(key => primary[key]).ToList();

        return new RowChange
        {
            Table = table,
            Kind = kind,
            KeyValues = keyValues,
            SourceValues = source is null ? EmptyValues() : ToDictionary(source, columns),
            TargetValues = target is null ? EmptyValues() : ToDictionary(target, columns),
            ChangedColumns = changed ?? Array.Empty<ColumnValueChange>(),
            KeyDisplay = BuildKeyDisplay(table, keyValues),
        };
    }

    private static IReadOnlyDictionary<string, object?> EmptyValues()
        => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, object?> ToDictionary(RowBuffer row, IReadOnlyList<ColumnSchemaInfo> columns)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
            dictionary[column.Name] = row[column.Name];

        return dictionary;
    }

    private static string BuildKeyDisplay(TableSchemaInfo table, IReadOnlyList<object?> keyValues)
    {
        var parts = new List<string>(table.KeyColumns.Count);
        for (var i = 0; i < table.KeyColumns.Count; i++)
        {
            var value = i < keyValues.Count ? keyValues[i] : null;
            var text = value is null or DBNull
                ? "NULL"
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            parts.Add($"{table.KeyColumns[i]}={text}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Caches the current row of a reader and exposes values by column name.</summary>
    private sealed class RowBuffer
    {
        private readonly SqlDataReader _reader;
        private readonly Dictionary<string, int> _ordinals;

        public RowBuffer(SqlDataReader reader, IReadOnlyList<ColumnSchemaInfo> columns)
        {
            _reader = reader;

            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;

            foreach (var column in columns)
                _ = ordinals.TryAdd(column.Name, -1);

            _ordinals = ordinals;
        }

        public bool Read() => _reader.Read();

        public object? this[string columnName]
        {
            get
            {
                if (!_ordinals.TryGetValue(columnName, out var ordinal) || ordinal < 0)
                    return null;

                return _reader.IsDBNull(ordinal) ? null : _reader.GetValue(ordinal);
            }
        }
    }
}
