namespace SqlSuite.Core.Models;

/// <summary>A table together with the key that identifies its rows.</summary>
public sealed class TableSchemaInfo
{
    public string SchemaName { get; init; } = string.Empty;

    public string TableName { get; init; } = string.Empty;

    /// <summary>Bracketed, fully qualified name such as <c>[dbo].[Products]</c>.</summary>
    public string QualifiedName => $"[{SchemaName}].[{TableName}]";

    /// <summary>Unbracketed qualified name used for lookups and exclusions.</summary>
    public string FullName => $"{SchemaName}.{TableName}";

    public IReadOnlyList<ColumnSchemaInfo> Columns { get; init; } = Array.Empty<ColumnSchemaInfo>();

    /// <summary>Columns that make up the row identity, in key order. Empty when no key was found.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();

    /// <summary>Where the key came from: <c>Primary key</c>, <c>Unique constraint</c> or <c>Unique index</c>.</summary>
    public string KeySource { get; init; } = string.Empty;

    /// <summary>Reason a configured comparison key could not be validated against this table.</summary>
    public string? KeySelectionError { get; init; }

    public bool HasKey => KeyColumns.Count > 0;

    public bool HasIdentity => Columns.Any(c => c.IsIdentity);

    public ColumnSchemaInfo? FindColumn(string name)
        => Columns.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => HasKey
        ? $"{FullName} ({KeySource}: {string.Join(", ", KeyColumns)})"
        : $"{FullName} (no comparable key)";
}
