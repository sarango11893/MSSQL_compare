namespace SqlSuite.Core.Models;

/// <summary>A single column whose value differs between source and target.</summary>
public sealed record ColumnValueChange(string ColumnName, object? SourceValue, object? TargetValue);
