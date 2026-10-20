using SqlSuite.Core.Models;

namespace SqlSuite.Web.ViewModels;

/// <summary>One table and the matching rows to display in the DML comparison list.</summary>
public sealed record FilteredDataTableGroup(
    TableDataDifference Table,
    IReadOnlyList<RowChange> Changes);
