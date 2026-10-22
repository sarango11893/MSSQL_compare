namespace SqlSuite.Core.Models;

/// <summary>Explicit row-identity columns used to match rows in a DML comparison.</summary>
public sealed class TableKeyMapping
{
    public string Table { get; set; } = string.Empty;

    public IList<string> Columns { get; set; } = new List<string>();
}
