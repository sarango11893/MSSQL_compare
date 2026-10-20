using SqlSuite.Core.Enums;

namespace SqlSuite.Core.Models;

/// <summary>Row-level differences for one table.</summary>
public sealed class TableDataDifference
{
    public required TableSchemaInfo Table { get; init; }

    public IReadOnlyList<RowChange> Changes { get; init; } = Array.Empty<RowChange>();

    /// <summary>Reason the table could not be compared (missing key, missing on one side, row limit…).</summary>
    public string? SkipReason { get; init; }

    /// <summary>Non fatal issues, for example a truncated result set.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool IsTruncated { get; init; }

    public bool IsSkipped => SkipReason is not null;

    public int InsertCount => Changes.Count(c => c.Kind == RowChangeKind.Insert);

    public int UpdateCount => Changes.Count(c => c.Kind == RowChangeKind.Update);

    public int DeleteCount => Changes.Count(c => c.Kind == RowChangeKind.Delete);

    public int TotalCount => Changes.Count;

    public string Summary => IsSkipped
        ? $"Omitida: {SkipReason}"
        : $"{InsertCount} inserciones, {UpdateCount} actualizaciones, {DeleteCount} eliminaciones";

    public override string ToString() => $"{Table.FullName}: {Summary}";
}
