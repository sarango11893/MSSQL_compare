using System.Globalization;
using Microsoft.SqlServer.Dac.Model;
using SqlSuite.Core.Models;
using Index = Microsoft.SqlServer.Dac.Model.Index;

namespace SqlSuite.Core.Services;

/// <summary>
/// Reads table, column and key metadata out of a DacFx <see cref="TSqlModel"/>.
/// </summary>
internal static class DacModelReader
{
    private static readonly HashSet<string> NonComparableSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys",
        "INFORMATION_SCHEMA",
    };

    /// <summary>Returns every user table of the model paired with the key that identifies its rows.</summary>
    public static IReadOnlyList<TableSchemaInfo> ReadTables(TSqlModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var primaryKeys = ReadKeys(model, ModelSchema.PrimaryKeyConstraint, PrimaryKeyConstraint.Host, PrimaryKeyConstraint.Columns);
        var uniqueConstraints = ReadKeys(model, ModelSchema.UniqueConstraint, UniqueConstraint.Host, UniqueConstraint.Columns);
        var uniqueIndexes = ReadUniqueIndexes(model);

        var result = new List<TableSchemaInfo>();

        foreach (var table in model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table))
        {
            var parts = table.Name.Parts.ToList();
            if (parts.Count < 2)
                continue;

            var schemaName = parts[^2];
            var tableName = parts[^1];
            var fullName = $"{schemaName}.{tableName}";

            if (NonComparableSchemas.Contains(schemaName))
                continue;

            var columns = ReadColumns(table);

            // A table is only comparable when a key exists and every key column is nullable-free:
            // NULL values are never equal to each other, so NULL keys cannot be matched reliably.
            string keySource = "Origen desconocido";
            IReadOnlyList<string> keyColumns = Array.Empty<string>();

            if (primaryKeys.TryGetValue(fullName, out var pk) && IsUsableKey(pk, columns))
            {
                keyColumns = pk;
                keySource = "Primary key";
            }
            else if (uniqueConstraints.TryGetValue(fullName, out var constraint) && IsUsableKey(constraint, columns))
            {
                keyColumns = constraint;
                keySource = "Unique constraint";
            }
            else if (uniqueIndexes.TryGetValue(fullName, out var index) && IsUsableKey(index, columns))
            {
                keyColumns = index;
                keySource = "Unique index";
            }

            result.Add(new TableSchemaInfo
            {
                SchemaName = schemaName,
                TableName = tableName,
                Columns = columns,
                KeyColumns = keyColumns,
                KeySource = keyColumns.Count > 0 ? keySource : string.Empty,
            });
        }

        return result;
    }

    private static bool IsUsableKey(IReadOnlyList<string> keyColumns, IReadOnlyList<ColumnSchemaInfo> columns)
    {
        if (keyColumns.Count == 0)
            return false;

        foreach (var name in keyColumns)
        {
            var column = columns.FirstOrDefault(c =>
                c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (column is null || column.IsNullable || column.IsComputed)
                return false;
        }

        return true;
    }

    private static IReadOnlyList<ColumnSchemaInfo> ReadColumns(TSqlObject table)
    {
        var columns = new List<ColumnSchemaInfo>();

        foreach (var column in table.GetReferenced(Table.Columns))
        {
            var dataType = column.GetReferenced(Column.DataType).FirstOrDefault();
            var dataTypeName = dataType?.Name.Parts.LastOrDefault() ?? string.Empty;
            var expression = Get<string>(column, Column.Expression);

            columns.Add(new ColumnSchemaInfo
            {
                Name = column.Name.Parts.Last(),
                DataTypeName = dataTypeName,
                MaxLength = Get<int>(column, Column.Length),
                IsMax = Get<bool>(column, Column.IsMax),
                Precision = Get<int>(column, Column.Precision),
                Scale = Get<int>(column, Column.Scale),
                IsNullable = Get<bool>(column, Column.Nullable),
                IsIdentity = Get<bool>(column, Column.IsIdentity),
                IsComputed = !string.IsNullOrWhiteSpace(expression),
                IsRowVersion = dataTypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase)
                    || dataTypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase),
            });
        }

        return columns;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadKeys(
        TSqlModel model,
        ModelTypeClass type,
        ModelRelationshipClass hostRelationship,
        ModelRelationshipClass columnRelationship)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var constraint in model.GetObjects(DacQueryScopes.UserDefined, type))
        {
            var host = constraint.GetReferenced(hostRelationship).FirstOrDefault();
            if (host is null)
                continue;

            var hostName = GetQualifiedName(host);
            if (hostName is null)
                continue;

            var columns = constraint.GetReferenced(columnRelationship)
                .Select(c => c.Name.Parts.Last())
                .ToList();

            // Keep the narrowest key when a table has several candidates.
            if (!map.TryGetValue(hostName, out var existing) || columns.Count < existing.Count)
                map[hostName] = columns;
        }

        return map;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadUniqueIndexes(TSqlModel model)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Index))
        {
            if (!Get<bool>(index, Index.Unique))
                continue;

            // A filtered index only covers a subset of rows, so it cannot identify all of them.
            if (!string.IsNullOrWhiteSpace(Get<string>(index, Index.FilterPredicate)))
                continue;

            var host = index.GetReferenced(Index.IndexedObject).FirstOrDefault();
            if (host is null || host.ObjectType != ModelSchema.Table)
                continue;

            var hostName = GetQualifiedName(host);
            if (hostName is null)
                continue;

            var columns = index.GetReferenced(Index.Columns)
                .Select(c => c.Name.Parts.Last())
                .ToList();

            if (!map.TryGetValue(hostName, out var existing) || columns.Count < existing.Count)
                map[hostName] = columns;
        }

        return map;
    }

    private static string? GetQualifiedName(TSqlObject table)
    {
        var parts = table.Name.Parts.ToList();
        return parts.Count < 2 ? null : $"{parts[^2]}.{parts[^1]}";
    }

    private static T Get<T>(TSqlObject modelObject, ModelPropertyClass property)
    {
        object? raw;
        try
        {
            raw = modelObject.GetProperty(property);
        }
        catch (Exception)
        {
            return default!;
        }

        if (raw is null)
            return default!;

        if (raw is T typed)
            return typed;

        try
        {
            return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return default!;
        }
    }
}
