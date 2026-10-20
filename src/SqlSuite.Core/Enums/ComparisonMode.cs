namespace SqlSuite.Core.Enums;

/// <summary>
/// Selects which comparison engine the application drives.
/// </summary>
public enum ComparisonMode
{
    /// <summary>DDL comparison: tables, columns, constraints, indexes, programmable objects.</summary>
    Schema = 0,

    /// <summary>DML comparison: row level Insert / Update / Delete detection.</summary>
    Data = 1,
}
