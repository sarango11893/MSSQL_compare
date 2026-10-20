using System.Text;
using SqlSuite.Core.Enums;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

/// <summary>
/// Turns classified row differences into a deployable T-SQL script.
/// Kept free of any database access so the output can be unit tested.
/// </summary>
public sealed class DataScriptGenerator
{
    private readonly ComparisonOptions _options;

    public DataScriptGenerator(ComparisonOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Generates the synchronisation script, or an empty string when there is nothing to do.</summary>
    public string Generate(IEnumerable<TableDataDifference> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var comparable = tables
            .Where(t => !t.IsSkipped && t.TotalCount > 0)
            .ToList();

        if (comparable.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("-- ============================================================");
        builder.AppendLine("-- SqlSuite - script de sincronización de datos (DML)");
        builder.AppendLine($"-- Generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"-- Tablas con cambios: {comparable.Count}");
        builder.AppendLine($"-- Inserciones: {comparable.Sum(t => t.InsertCount)}, " +
                           $"Actualizaciones: {comparable.Sum(t => t.UpdateCount)}, " +
                           $"Eliminaciones: {comparable.Sum(t => t.DeleteCount)}");
        builder.AppendLine("-- ============================================================");
        builder.AppendLine();
        builder.AppendLine("SET NOCOUNT ON;");
        builder.AppendLine("SET XACT_ABORT ON;");
        builder.AppendLine();

        if (_options.UseTransactionForDataScript)
        {
            builder.AppendLine("BEGIN TRANSACTION;");
            builder.AppendLine();
        }

        foreach (var table in comparable)
            AppendTable(builder, table);

        if (_options.UseTransactionForDataScript)
        {
            builder.AppendLine("COMMIT TRANSACTION;");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private void AppendTable(StringBuilder builder, TableDataDifference table)
    {
        var info = table.Table;

        builder.AppendLine("-- ------------------------------------------------------------");
        builder.AppendLine($"-- {info.QualifiedName}  ({info.KeySource}: {string.Join(", ", info.KeyColumns)})");
        if (_options.IncludeChangeSummaryComments)
        {
            builder.AppendLine($"-- {table.InsertCount} inserción(es), {table.UpdateCount} actualización(es), {table.DeleteCount} eliminación(es)");
        }

        builder.AppendLine("-- ------------------------------------------------------------");

        var deletes = table.Changes.Where(c => c.Kind == RowChangeKind.Delete).ToList();
        var updates = table.Changes.Where(c => c.Kind == RowChangeKind.Update).ToList();
        var inserts = table.Changes.Where(c => c.Kind == RowChangeKind.Insert).ToList();

        foreach (var change in deletes)
        {
            builder.AppendLine($"DELETE FROM {info.QualifiedName} WHERE {BuildKeyPredicate(info, change.KeyValues)};");
        }

        if (deletes.Count > 0)
            builder.AppendLine();

        foreach (var change in updates)
            AppendUpdate(builder, info, change);

        if (updates.Count > 0)
            builder.AppendLine();

        if (inserts.Count > 0)
            AppendInserts(builder, info, inserts);

        builder.AppendLine();
    }

    private static void AppendUpdate(StringBuilder builder, TableSchemaInfo table, RowChange change)
    {
        if (change.ChangedColumns.Count == 0)
            return;

        var assignments = change.ChangedColumns
            .Select(c =>
            {
                var column = table.FindColumn(c.ColumnName);
                return $"{SqlLiteralFormatter.QuoteIdentifier(c.ColumnName)} = {SqlLiteralFormatter.Format(c.SourceValue, column)}";
            });

        builder.AppendLine(
            $"UPDATE {table.QualifiedName} SET {string.Join(", ", assignments)} " +
            $"WHERE {BuildKeyPredicate(table, change.KeyValues)};");
    }

    private void AppendInserts(StringBuilder builder, TableSchemaInfo table, IReadOnlyList<RowChange> inserts)
    {
        var first = inserts[0];
        var columns = first.SourceValues.Keys.ToList();
        var useIdentityInsert = table.HasIdentity && _options.IncludeIdentityInsert;

        if (!useIdentityInsert)
        {
            columns = columns
                .Where(name => table.FindColumn(name)?.IsIdentity != true)
                .ToList();
        }

        if (columns.Count == 0)
            return;

        if (useIdentityInsert)
            builder.AppendLine($"SET IDENTITY_INSERT {table.QualifiedName} ON;");

        var columnList = string.Join(", ", columns.Select(SqlLiteralFormatter.QuoteIdentifier));

        foreach (var change in inserts)
        {
            var values = columns.Select(name =>
            {
                change.SourceValues.TryGetValue(name, out var value);
                return SqlLiteralFormatter.Format(value, table.FindColumn(name));
            });

            builder.AppendLine($"INSERT INTO {table.QualifiedName} ({columnList}) VALUES ({string.Join(", ", values)});");
        }

        if (useIdentityInsert)
            builder.AppendLine($"SET IDENTITY_INSERT {table.QualifiedName} OFF;");
    }

    private static string BuildKeyPredicate(TableSchemaInfo table, IReadOnlyList<object?> keyValues)
    {
        var parts = new List<string>(table.KeyColumns.Count);

        for (var i = 0; i < table.KeyColumns.Count; i++)
        {
            var name = table.KeyColumns[i];
            var value = i < keyValues.Count ? keyValues[i] : null;
            var literal = SqlLiteralFormatter.Format(value, table.FindColumn(name));
            parts.Add($"{SqlLiteralFormatter.QuoteIdentifier(name)} = {literal}");
        }

        return string.Join(" AND ", parts);
    }
}
