namespace SqlSuite.Core.Models;

public enum CodeDiffKind
{
    Unchanged,
    Removed,
    Added,
}

/// <summary>A paired line in the old/new object definition comparison.</summary>
public sealed record CodeDiffLine(
    int? OldLineNumber,
    string? OldText,
    int? NewLineNumber,
    string? NewText,
    CodeDiffKind Kind);
