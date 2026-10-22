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
        => CompareAsync(source, target, options, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Compares all comparable rows and reports progress as each table is streamed.</summary>
    public async Task<DataComparisonResultInfo> CompareAsync(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<DataComparisonProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var effective = (options ?? _defaultOptions).Clone();
        effective.MaxRowsPerTable = Math.Max(1, effective.MaxRowsPerTable);
        var startedAt = DateTime.UtcNow;
        var errors = new List<string>();
        var configuredTables = effective.IncludedTables
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var sourceConnection = new SqlConnection(source.GetConnectionString());
        using var targetConnection = new SqlConnection(target.GetConnectionString());
        IReadOnlyList<TableSchemaInfo> sourceMetadata;
        IReadOnlyList<TableSchemaInfo> targetMetadata;

        try
        {
            progress?.Report(new DataComparisonProgress("Conectando a origen"));
            await sourceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new DataComparisonProgress("Leyendo metadatos DML de origen"));
            sourceMetadata = await SqlTableMetadataReader.ReadTablesAsync(
                sourceConnection,
                configuredTables,
                effective.ComparisonKeys,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new DataComparisonProgress("Conectando a destino"));
            await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(new DataComparisonProgress("Leyendo metadatos DML de destino"));
            targetMetadata = await SqlTableMetadataReader.ReadTablesAsync(
                targetConnection,
                configuredTables,
                effective.ComparisonKeys,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DataComparisonResultInfo
            {
                IsValid = false,
                Errors = new[] { $"No se pudo leer metadatos DML de origen o destino: {FormatExceptionDetails(ex)}" },
                Duration = DateTime.UtcNow - startedAt,
            };
        }

        var allSourceTables = sourceMetadata
            .ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
        var targetTables = targetMetadata
            .ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
        var sourceTables = allSourceTables
            .Where(pair => IsIncluded(pair.Value, configuredTables))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var foundConfiguredTables = sourceTables.Values
            .SelectMany(table => configuredTables.Where(name => MatchesTableName(table, name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableDataDifference>();
        var skipped = new List<string>();
        var additionalWarnings = new List<string>();
        foreach (var missingName in configuredTables.Where(name => !foundConfiguredTables.Contains(name)))
            skipped.Add($"{missingName} (no se encontró en el esquema de origen)");

        if (configuredTables.Count > 0 && sourceTables.Count == 0)
        {
            return new DataComparisonResultInfo
            {
                IsValid = false,
                Errors = new[] { "Ninguna de las tablas configuradas existe en el esquema de origen." },
                SkippedTables = skipped,
                Duration = DateTime.UtcNow - startedAt,
            };
        }

        var canCompareAcrossDatabases = SameServer(source.ServerName, target.ServerName)
            && SameSecurityContext(source, target);

        var orderedTables = sourceTables.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        for (var tableIndex = 0; tableIndex < orderedTables.Count; tableIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (fullName, sourceTable) = orderedTables[tableIndex];

            progress?.Report(new DataComparisonProgress(
                "Revisando tablas",
                fullName,
                tableIndex + 1,
                orderedTables.Count));

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

            if (sourceTable.KeySelectionError is not null || targetTable.KeySelectionError is not null)
            {
                var reason = sourceTable.KeySelectionError ?? targetTable.KeySelectionError;
                skipped.Add($"{fullName} (clave de comparación no validada: {reason})");
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

            var displayColumns = sourceTable.Columns
                .Where(column => targetTable.FindColumn(column.Name) is not null)
                .ToList();

            try
            {
                TableDataDifference difference;
                if (canCompareAcrossDatabases
                    && targetTable.HasKey
                    && CanCompareInSqlServer(sourceTable, targetTable, comparableColumns, effective))
                {
                    try
                    {
                        difference = await CompareTableInSqlServerAsync(
                            sourceConnection,
                            source.DatabaseName,
                            target.DatabaseName,
                            sourceTable,
                            comparableColumns,
                            displayColumns,
                            effective,
                            cancellationToken,
                            tableIndex + 1,
                            orderedTables.Count,
                            progress).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        additionalWarnings.Add(
                            $"{fullName}: comparación dentro de SQL Server no disponible ({ex.Message}); se usó lectura por conexiones separadas.");
                        difference = await CompareTableAsync(
                            sourceConnection,
                            targetConnection,
                            sourceTable,
                            comparableColumns,
                            displayColumns,
                            effective,
                            cancellationToken,
                            tableIndex + 1,
                            orderedTables.Count,
                            progress).ConfigureAwait(false);
                    }
                }
                else
                {
                    difference = await CompareTableAsync(
                        sourceConnection,
                        targetConnection,
                        sourceTable,
                        comparableColumns,
                        displayColumns,
                        effective,
                        cancellationToken,
                        tableIndex + 1,
                        orderedTables.Count,
                        progress).ConfigureAwait(false);
                }

                tables.Add(difference);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{fullName}: {ex.Message}");
            }
        }

        var warnings = tables.SelectMany(t => t.Warnings).ToList();
        warnings.AddRange(additionalWarnings);
        var allEmpty = tables.All(t => t.TotalCount == 0);

        var hasTruncatedTables = tables.Any(table => table.IsTruncated);
        var script = effective.GenerateDeploymentScript && !allEmpty && !hasTruncatedTables
            ? new DataScriptGenerator(effective).Generate(tables)
            : string.Empty;

        return new DataComparisonResultInfo
        {
            IsValid = errors.Count == 0,
            // A skipped table is not a proven match, so it must not be reported as equality.
            IsEqual = allEmpty && errors.Count == 0 && skipped.Count == 0 && !hasTruncatedTables,
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
        return DacModelReader.ReadTables(LoadModel(connection, "base de datos")).ToList();
    }

    private static TSqlModel LoadModel(DatabaseConnectionInfo connection, string side)
    {
        try
        {
            return TSqlModel.LoadFromDatabase(
                connection.GetConnectionString(),
                new ModelExtractOptions { LoadAsScriptBackedModel = false });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Falló la extracción del esquema de {side}.", ex);
        }
    }

    private static string FormatExceptionDetails(Exception exception)
    {
        var details = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            details.Add($"{current.GetType().Name}: {current.Message}");

        return string.Join(" -> ", details);
    }

    private static bool IsExcluded(string fullName, ComparisonOptions options)
        => options.ExcludedTables.Any(e => e.Equals(fullName, StringComparison.OrdinalIgnoreCase));

    private static bool IsIncluded(TableSchemaInfo table, IReadOnlyList<string> includedTables)
        => includedTables.Count == 0 || includedTables.Any(name => MatchesTableName(table, name));

    private static bool MatchesTableName(TableSchemaInfo table, string configuredName)
    {
        var normalizedName = configuredName
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Trim();

        return normalizedName.Contains('.')
            ? table.FullName.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
            : table.TableName.Equals(normalizedName, StringComparison.OrdinalIgnoreCase);
    }

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

    private static async Task<TableDataDifference> CompareTableAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchemaInfo sourceTable,
        IReadOnlyList<ColumnSchemaInfo> comparableColumns,
        IReadOnlyList<ColumnSchemaInfo> displayColumns,
        ComparisonOptions options,
        CancellationToken cancellationToken,
        int tableIndex,
        int tableCount,
        IProgress<DataComparisonProgress>? progress)
    {
        var changes = new List<RowChange>();
        var truncated = false;
        long sourceRowsRead = 0;
        long targetRowsRead = 0;
        long nextProgressAt = 5_000;

        using var sourceCommand = CreateCommand(sourceConnection, sourceTable, displayColumns, options);
        using var targetCommand = CreateCommand(targetConnection, sourceTable, displayColumns, options);
        await using var sourceReader = await sourceCommand
            .ExecuteReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        await using var targetReader = await targetCommand
            .ExecuteReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        var sourceRow = new RowBuffer(sourceReader, displayColumns);
        var targetRow = new RowBuffer(targetReader, displayColumns);

        var hasSource = await ReadSourceRowAsync().ConfigureAwait(false);
        var hasTarget = await ReadTargetRowAsync().ConfigureAwait(false);

        while (hasSource || hasTarget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!hasSource)
            {
                changes.Add(BuildChange(
                    RowChangeKind.Delete,
                    sourceTable,
                    null,
                    targetRow,
                    comparableColumns,
                    displayColumns,
                    options));
                hasTarget = await ReadTargetRowAsync().ConfigureAwait(false);
            }
            else if (!hasTarget)
            {
                changes.Add(BuildChange(
                    RowChangeKind.Insert,
                    sourceTable,
                    sourceRow,
                    null,
                    comparableColumns,
                    displayColumns,
                    options));
                hasSource = await ReadSourceRowAsync().ConfigureAwait(false);
            }
            else
            {
                var comparison = CompareKeys(sourceRow, targetRow, sourceTable.KeyColumns, options);

                if (comparison < 0)
                {
                    changes.Add(BuildChange(
                        RowChangeKind.Insert,
                        sourceTable,
                        sourceRow,
                        null,
                        comparableColumns,
                        displayColumns,
                        options));
                    hasSource = await ReadSourceRowAsync().ConfigureAwait(false);
                }
                else if (comparison > 0)
                {
                    changes.Add(BuildChange(
                        RowChangeKind.Delete,
                        sourceTable,
                        null,
                        targetRow,
                        comparableColumns,
                        displayColumns,
                        options));
                    hasTarget = await ReadTargetRowAsync().ConfigureAwait(false);
                }
                else
                {
                    var changed = FindChangedColumns(sourceRow, targetRow, comparableColumns, options);
                    if (changed.Count > 0)
                    {
                        changes.Add(BuildChange(
                            RowChangeKind.Update,
                            sourceTable,
                            sourceRow,
                            targetRow,
                            comparableColumns,
                            displayColumns,
                            options,
                            changed));
                    }

                    hasSource = await ReadSourceRowAsync().ConfigureAwait(false);
                    hasTarget = await ReadTargetRowAsync().ConfigureAwait(false);
                }
            }

            var rowsRead = sourceRowsRead + targetRowsRead;
            if (rowsRead >= nextProgressAt)
            {
                progress?.Report(new DataComparisonProgress(
                    "Comparando filas",
                    sourceTable.FullName,
                    tableIndex,
                    tableCount,
                    sourceRowsRead,
                    targetRowsRead));
                nextProgressAt = rowsRead + 5_000;
            }

            if (changes.Count >= options.MaxRowsPerTable)
            {
                truncated = true;
                break;
            }
        }

        progress?.Report(new DataComparisonProgress(
            "Tabla completada",
            sourceTable.FullName,
            tableIndex,
            tableCount,
            sourceRowsRead,
            targetRowsRead));

        return new TableDataDifference
        {
            Table = sourceTable,
            Changes = changes,
            IsTruncated = truncated,
            Warnings = truncated
                ? new[] { $"Se alcanzó el límite de {options.MaxRowsPerTable} filas; el resultado está truncado." }
                : Array.Empty<string>(),
        };

        async Task<bool> ReadSourceRowAsync()
        {
            var hasRow = await sourceRow.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (hasRow)
                sourceRowsRead++;
            return hasRow;
        }

        async Task<bool> ReadTargetRowAsync()
        {
            var hasRow = await targetRow.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (hasRow)
                targetRowsRead++;
            return hasRow;
        }
    }

    private static async Task<TableDataDifference> CompareTableInSqlServerAsync(
        SqlConnection connection,
        string sourceDatabase,
        string targetDatabase,
        TableSchemaInfo table,
        IReadOnlyList<ColumnSchemaInfo> comparableColumns,
        IReadOnlyList<ColumnSchemaInfo> displayColumns,
        ComparisonOptions options,
        CancellationToken cancellationToken,
        int tableIndex,
        int tableCount,
        IProgress<DataComparisonProgress>? progress)
    {
        progress?.Report(new DataComparisonProgress(
            "Comparando diferencias en SQL Server",
            table.FullName,
            tableIndex,
            tableCount));

        using var command = CreateServerSideCommand(
            connection,
            sourceDatabase,
            targetDatabase,
            table,
            comparableColumns,
            displayColumns,
            options);
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);

        var selected = table.KeyColumns
            .Concat(displayColumns.Select(column => column.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceOrdinals = selected
            .Select((column, index) => (Name: column, Ordinal: index * 2))
            .ToDictionary(item => item.Name, item => item.Ordinal, StringComparer.OrdinalIgnoreCase);
        var targetOrdinals = selected
            .Select((column, index) => (Name: column, Ordinal: index * 2 + 1))
            .ToDictionary(item => item.Name, item => item.Ordinal, StringComparer.OrdinalIgnoreCase);
        var sourceKeyOrdinal = sourceOrdinals[table.KeyColumns[0]];
        var targetKeyOrdinal = targetOrdinals[table.KeyColumns[0]];
        var changes = new List<RowChange>();
        var truncated = false;
        long rowsRead = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsRead++;

            var sourceSelectedValues = ReadRowValues(reader, sourceOrdinals, selected);
            var targetSelectedValues = ReadRowValues(reader, targetOrdinals, selected);
            var sourceValues = SelectValues(sourceSelectedValues, displayColumns);
            var targetValues = SelectValues(targetSelectedValues, displayColumns);
            var sourceExists = !reader.IsDBNull(sourceKeyOrdinal);
            var targetExists = !reader.IsDBNull(targetKeyOrdinal);
            RowChange? change;

            if (!sourceExists)
            {
                change = CreateServerSideChange(
                    RowChangeKind.Delete,
                    table,
                    comparableColumns,
                    displayColumns,
                    null,
                    targetValues,
                    targetSelectedValues);
            }
            else if (!targetExists)
            {
                change = CreateServerSideChange(
                    RowChangeKind.Insert,
                    table,
                    comparableColumns,
                    displayColumns,
                    sourceValues,
                    null,
                    sourceSelectedValues);
            }
            else
            {
                var changedColumns = comparableColumns
                    .Select(column => new ColumnValueChange(
                        column.Name,
                        sourceValues[column.Name],
                        targetValues[column.Name]))
                    .Where(valueChange => CompareValues(
                        valueChange.SourceValue,
                        valueChange.TargetValue,
                        options,
                        normalizeLineEndings: options.NormalizeLineEndings) != 0)
                    .ToList();

                change = changedColumns.Count == 0
                    ? null
                    : CreateServerSideChange(
                        RowChangeKind.Update,
                        table,
                        comparableColumns,
                        displayColumns,
                        sourceValues,
                        targetValues,
                        sourceSelectedValues,
                        changedColumns);
            }

            if (change is not null)
            {
                if (changes.Count == options.MaxRowsPerTable)
                {
                    truncated = true;
                    break;
                }

                changes.Add(change);
            }

            if (rowsRead % 1_000 == 0)
            {
                progress?.Report(new DataComparisonProgress(
                    "Diferencias recibidas de SQL Server",
                    table.FullName,
                    tableIndex,
                    tableCount,
                    rowsRead,
                    rowsRead));
            }
        }

        progress?.Report(new DataComparisonProgress(
            "Tabla completada",
            table.FullName,
            tableIndex,
            tableCount,
            rowsRead,
            rowsRead));

        return new TableDataDifference
        {
            Table = table,
            Changes = changes,
            IsTruncated = truncated,
            Warnings = truncated
                ? new[] { $"Se alcanzó el límite de {options.MaxRowsPerTable} filas; el resultado está truncado." }
                : Array.Empty<string>(),
        };
    }

    private static SqlCommand CreateServerSideCommand(
        SqlConnection connection,
        string sourceDatabase,
        string targetDatabase,
        TableSchemaInfo table,
        IReadOnlyList<ColumnSchemaInfo> comparableColumns,
        IReadOnlyList<ColumnSchemaInfo> displayColumns,
        ComparisonOptions options)
    {
        var selected = table.KeyColumns
            .Concat(displayColumns.Select(column => column.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceTable = $"{SqlLiteralFormatter.QuoteIdentifier(sourceDatabase)}.{table.QualifiedName}";
        var targetTable = $"{SqlLiteralFormatter.QuoteIdentifier(targetDatabase)}.{table.QualifiedName}";
        var join = string.Join(" AND ", table.KeyColumns.Select(key =>
        {
            var column = table.FindColumn(key)!;
            var sourceKey = $"s.{SqlLiteralFormatter.QuoteIdentifier(key)}";
            var targetKey = $"t.{SqlLiteralFormatter.QuoteIdentifier(key)}";
            return BuildSqlEquality(sourceKey, targetKey, column);
        }));
        var sourcePresence = $"s.{SqlLiteralFormatter.QuoteIdentifier(table.KeyColumns[0])} IS NULL";
        var targetPresence = $"t.{SqlLiteralFormatter.QuoteIdentifier(table.KeyColumns[0])} IS NULL";
        var differentColumns = string.Join(" OR ", comparableColumns.Select(column =>
        {
            var sourceValue = $"s.{SqlLiteralFormatter.QuoteIdentifier(column.Name)}";
            var targetValue = $"t.{SqlLiteralFormatter.QuoteIdentifier(column.Name)}";
            return BuildSqlDifference(sourceValue, targetValue, column);
        }));

        var query = new StringBuilder();
        query.Append("SELECT ");
        query.Append(string.Join(", ", selected.SelectMany((name, index) =>
        {
            var quoted = SqlLiteralFormatter.QuoteIdentifier(name);
            return new[] { $"s.{quoted} AS [s{index}]", $"t.{quoted} AS [t{index}]" };
        })));
        query.Append(" FROM ");
        query.Append(sourceTable);
        query.Append(" AS s FULL OUTER JOIN ");
        query.Append(targetTable);
        query.Append(" AS t ON ");
        query.Append(join);
        query.Append(" WHERE ");
        query.Append(sourcePresence);
        query.Append(" OR ");
        query.Append(targetPresence);
        query.Append(" OR (");
        query.Append(string.Join(" AND ", table.KeyColumns.Select(key =>
        {
            var quoted = SqlLiteralFormatter.QuoteIdentifier(key);
            return $"s.{quoted} IS NOT NULL AND t.{quoted} IS NOT NULL";
        })));
        query.Append(" AND (");
        query.Append(differentColumns);
        query.Append("))");

        var command = connection.CreateCommand();
        command.CommandText = query.ToString();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        return command;
    }

    private static IReadOnlyDictionary<string, object?> ReadRowValues(
        SqlDataReader reader,
        IReadOnlyDictionary<string, int> ordinals,
        IReadOnlyList<string> columns)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in columns)
        {
            var ordinal = ordinals[name];
            values[name] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        return values;
    }

    private static IReadOnlyDictionary<string, object?> SelectValues(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ColumnSchemaInfo> columns)
        => columns.ToDictionary(column => column.Name, column => values[column.Name], StringComparer.OrdinalIgnoreCase);

    private static RowChange CreateServerSideChange(
        RowChangeKind kind,
        TableSchemaInfo table,
        IReadOnlyList<ColumnSchemaInfo> comparableColumns,
        IReadOnlyList<ColumnSchemaInfo> displayColumns,
        IReadOnlyDictionary<string, object?>? source,
        IReadOnlyDictionary<string, object?>? target,
        IReadOnlyDictionary<string, object?> keySourceValues,
        IReadOnlyList<ColumnValueChange>? changedColumns = null)
    {
        var keyValues = table.KeyColumns
            .Select(key => (target ?? source!)[key])
            .ToList();
        return new RowChange
        {
            Table = table,
            Kind = kind,
            KeyValues = keyValues,
            SourceValues = source is null ? EmptyValues() : SelectValues(source, comparableColumns),
            TargetValues = target is null ? EmptyValues() : SelectValues(target, comparableColumns),
            PreviewSourceValues = source is null ? EmptyValues() : SelectValues(source, displayColumns),
            PreviewTargetValues = target is null ? EmptyValues() : SelectValues(target, displayColumns),
            ChangedColumns = changedColumns ?? Array.Empty<ColumnValueChange>(),
            KeyDisplay = BuildKeyDisplay(table, keyValues),
        };
    }

    private static bool CanCompareInSqlServer(
        TableSchemaInfo source,
        TableSchemaInfo target,
        IReadOnlyList<ColumnSchemaInfo> columns,
        ComparisonOptions options)
    {
        if (!source.KeyColumns.SequenceEqual(target.KeyColumns, StringComparer.OrdinalIgnoreCase))
            return false;

        foreach (var name in source.KeyColumns)
        {
            var sourceColumn = source.FindColumn(name);
            var targetColumn = target.FindColumn(name);
            if (sourceColumn is null || targetColumn is null
                || !SameSqlType(sourceColumn, targetColumn)
                || options.NormalizeLineEndings && IsStringType(sourceColumn)
                || !IsSqlComparableType(sourceColumn))
                return false;
        }

        foreach (var column in columns)
        {
            var targetColumn = target.FindColumn(column.Name);
            if (targetColumn is null
                || !SameSqlType(column, targetColumn)
                || !IsSqlComparableType(column))
                return false;
        }

        return columns.Count > 0;
    }

    private static bool SameSqlType(ColumnSchemaInfo left, ColumnSchemaInfo right)
        => left.DataTypeName.Equals(right.DataTypeName, StringComparison.OrdinalIgnoreCase)
           && left.MaxLength == right.MaxLength
           && left.Precision == right.Precision
           && left.Scale == right.Scale
           && left.IsMax == right.IsMax;

    internal static bool IsSqlComparableType(ColumnSchemaInfo column)
    {
        var type = column.DataTypeName.ToLowerInvariant();
        if (type is "varchar" or "nvarchar" or "char" or "nchar" or "sysname")
            return true;

        return type is "bit" or "tinyint" or "smallint" or "int" or "bigint"
            or "decimal" or "numeric" or "money" or "smallmoney"
            or "float" or "real"
            or "date" or "time" or "smalldatetime" or "datetime" or "datetime2" or "datetimeoffset"
            or "uniqueidentifier"
            or "binary" or "varbinary" or "timestamp" or "rowversion";
    }

    internal static string BuildSqlEquality(string left, string right, ColumnSchemaInfo column)
    {
        if (IsStringType(column))
        {
            return $"{left} COLLATE Latin1_General_100_BIN2 = {right} COLLATE Latin1_General_100_BIN2 "
                + $"AND DATALENGTH({left}) = DATALENGTH({right})";
        }

        if (IsBinaryType(column))
            return $"{left} = {right} AND DATALENGTH({left}) = DATALENGTH({right})";

        return $"{left} = {right}";
    }

    internal static string BuildSqlDifference(string left, string right, ColumnSchemaInfo column)
    {
        var unequal = IsStringType(column)
            ? $"({left} COLLATE Latin1_General_100_BIN2 <> {right} COLLATE Latin1_General_100_BIN2 "
                + $"OR DATALENGTH({left}) <> DATALENGTH({right}))"
            : IsBinaryType(column)
                ? $"({left} <> {right} OR DATALENGTH({left}) <> DATALENGTH({right}))"
                : $"{left} <> {right}";

        return $"({unequal} OR ({left} IS NULL AND {right} IS NOT NULL) OR ({left} IS NOT NULL AND {right} IS NULL))";
    }

    private static bool IsStringType(ColumnSchemaInfo column)
        => column.DataTypeName.Equals("varchar", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("char", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("nchar", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("sysname", StringComparison.OrdinalIgnoreCase);

    private static bool IsBinaryType(ColumnSchemaInfo column)
        => column.DataTypeName.Equals("binary", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("varbinary", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
           || column.DataTypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase);

    private static bool SameServer(string source, string target)
        => string.Equals(
            source.Trim().Replace("(local)", ".", StringComparison.OrdinalIgnoreCase),
            target.Trim().Replace("(local)", ".", StringComparison.OrdinalIgnoreCase),
            StringComparison.OrdinalIgnoreCase);

    private static bool SameSecurityContext(DatabaseConnectionInfo source, DatabaseConnectionInfo target)
        => source.UseIntegratedSecurity == target.UseIntegratedSecurity
           && (source.UseIntegratedSecurity
               || (string.Equals(source.Username, target.Username, StringComparison.Ordinal)
                   && string.Equals(source.Password, target.Password, StringComparison.Ordinal)));

    private static SqlCommand CreateCommand(
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

        return command;
    }

    private static int CompareKeys(RowBuffer source, RowBuffer target, IReadOnlyList<string> keyColumns, ComparisonOptions options)
    {
        foreach (var key in keyColumns)
        {
            var left = source[key];
            var right = target[key];
            var result = CompareValues(left, right, options, options.NormalizeLineEndings);
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

            if (CompareValues(left, right, options, normalizeLineEndings: options.NormalizeLineEndings) != 0)
                changed.Add(new ColumnValueChange(column.Name, left, right));
        }

        return changed;
    }

    /// <summary>Compares values, optionally treating CRLF and LF as equivalent in strings.</summary>
    internal static int CompareValues(
        object? left,
        object? right,
        ComparisonOptions options,
        bool normalizeLineEndings = false)
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
            if (normalizeLineEndings)
            {
                leftString = NormalizeLineEndings(leftString);
                rightString = NormalizeLineEndings(rightString);
            }

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

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);

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
        IReadOnlyList<ColumnSchemaInfo> comparableColumns,
        IReadOnlyList<ColumnSchemaInfo> displayColumns,
        ComparisonOptions options,
        IReadOnlyList<ColumnValueChange>? changed = null)
    {
        var primary = target ?? source!;
        var keyValues = table.KeyColumns.Select(key => primary[key]).ToList();

        return new RowChange
        {
            Table = table,
            Kind = kind,
            KeyValues = keyValues,
            SourceValues = source is null ? EmptyValues() : ToDictionary(source, comparableColumns),
            TargetValues = target is null ? EmptyValues() : ToDictionary(target, comparableColumns),
            PreviewSourceValues = source is null ? EmptyValues() : ToDictionary(source, displayColumns),
            PreviewTargetValues = target is null ? EmptyValues() : ToDictionary(target, displayColumns),
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

        public Task<bool> ReadAsync(CancellationToken cancellationToken)
            => _reader.ReadAsync(cancellationToken);

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
