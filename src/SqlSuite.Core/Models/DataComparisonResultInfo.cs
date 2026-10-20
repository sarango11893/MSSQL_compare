using SqlSuite.Core.Enums;

namespace SqlSuite.Core.Models;

/// <summary>Aggregate result of a data (DML) comparison.</summary>
public sealed class DataComparisonResultInfo
{
    public bool IsValid { get; init; }

    /// <summary>
    /// True only when every discovered table could be compared and none of them had differences.
    /// A skipped table is reported as a difference because it usually hides real divergence.
    /// </summary>
    public bool IsEqual { get; init; }

    public string Script { get; init; } = string.Empty;

    public bool HasScript => !string.IsNullOrWhiteSpace(Script);

    public IReadOnlyList<TableDataDifference> Tables { get; init; } = Array.Empty<TableDataDifference>();

    /// <summary>Tables that were considered for the comparison but could not take part.</summary>
    public IReadOnlyList<string> SkippedTables { get; init; } = Array.Empty<string>();

    public bool HasSkippedTables => SkippedTables.Count > 0;

    /// <summary>True when every table could be compared, so the verdict covers the whole database.</summary>
    public bool IsComplete => IsValid && !HasSkippedTables;

    public bool HasDifferences => Tables.Any(t => t.TotalCount > 0);

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public TimeSpan Duration { get; init; }

    public int InsertCount => Tables.Sum(t => t.InsertCount);

    public int UpdateCount => Tables.Sum(t => t.UpdateCount);

    public int DeleteCount => Tables.Sum(t => t.DeleteCount);

    public int TotalChangeCount => InsertCount + UpdateCount + DeleteCount;

    public IEnumerable<RowChange> AllChanges => Tables.SelectMany(t => t.Changes);

    public string Summary
    {
        get
        {
            if (!IsValid)
                return "La comparación de datos no es válida.";

            if (IsEqual)
                return $"Datos idénticos en {Tables.Count} tabla(s).";

            if (!HasDifferences)
                return $"Sin diferencias en {Tables.Count} tabla(s), pero {SkippedTables.Count} tabla(s) no se pudieron comparar.";

            var skipped = SkippedTables.Count == 0
                ? string.Empty
                : $" ({SkippedTables.Count} tabla(s) omitidas)";

            return $"{InsertCount} inserciones, {UpdateCount} actualizaciones y {DeleteCount} eliminaciones en {Tables.Count} tabla(s){skipped}.";
        }
    }

    public static DataComparisonResultInfo Invalid(string error) => new()
    {
        IsValid = false,
        IsEqual = false,
        Errors = new[] { error },
    };
}
