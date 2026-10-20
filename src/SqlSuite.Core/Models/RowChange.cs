using SqlSuite.Core.Enums;

namespace SqlSuite.Core.Models;

/// <summary>A row-level difference between the source and the target.</summary>
public sealed class RowChange
{
    public required TableSchemaInfo Table { get; init; }

    public required RowChangeKind Kind { get; init; }

    /// <summary>Key values in <see cref="TableSchemaInfo.KeyColumns"/> order.</summary>
    public IReadOnlyList<object?> KeyValues { get; init; } = Array.Empty<object?>();

    /// <summary>Values of the source row. Empty for <see cref="RowChangeKind.Delete"/>.</summary>
    public IReadOnlyDictionary<string, object?> SourceValues { get; init; }
        = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Values of the target row. Empty for <see cref="RowChangeKind.Insert"/>.</summary>
    public IReadOnlyDictionary<string, object?> TargetValues { get; init; }
        = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Columns that changed. Only populated for <see cref="RowChangeKind.Update"/>.</summary>
    public IReadOnlyList<ColumnValueChange> ChangedColumns { get; init; } = Array.Empty<ColumnValueChange>();

    /// <summary>Readable key, for example <c>Id=42</c>.</summary>
    public string KeyDisplay { get; init; } = string.Empty;

    public override string ToString() => $"{Kind} {Table.FullName} {KeyDisplay}";
}
