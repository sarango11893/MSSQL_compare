namespace SqlSuite.Core.Models;

/// <summary>Progress while the exact DML comparison discovers schemas and streams table rows.</summary>
public sealed record DataComparisonProgress(
    string Stage,
    string? TableName = null,
    int TableIndex = 0,
    int TableCount = 0,
    long SourceRowsRead = 0,
    long TargetRowsRead = 0);
