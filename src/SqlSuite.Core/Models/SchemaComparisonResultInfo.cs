namespace SqlSuite.Core.Models;

/// <summary>Outcome of a schema comparison, ready to be bound to the UI.</summary>
public sealed class SchemaComparisonResultInfo
{
    public bool IsValid { get; init; }

    public bool IsEqual { get; init; }

    /// <summary>The deployment T-SQL script, dependency ordered.</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary><see langword="true"/> when <see cref="Script"/> contains executable statements.</summary>
    public bool HasScript => !string.IsNullOrWhiteSpace(Script);

    public IReadOnlyList<SchemaDifferenceInfo> Differences { get; init; } = Array.Empty<SchemaDifferenceInfo>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Total number of nodes in the difference tree, including property leaves.</summary>
    public int TotalDifferenceCount { get; init; }

    /// <summary>Number of top level objects selected by the user (all of them on a fresh comparison).</summary>
    public int SelectedObjectCount { get; init; }

    /// <summary>Total number of top level objects in the difference tree.</summary>
    public int TotalObjectCount { get; init; }

    /// <summary>Top level objects the user unchecked, therefore absent from <see cref="Script"/>.</summary>
    public int ExcludedObjectCount { get; init; }

    /// <summary><see langword="true"/> when the user unchecked at least one object.</summary>
    public bool IsPartial => ExcludedObjectCount > 0;

    public TimeSpan Duration { get; init; }

    /// <summary>Summary such as <c>3 tables added, 1 changed</c>.</summary>
    public string Summary { get; init; } = string.Empty;

    public override string ToString() => Summary;
}
