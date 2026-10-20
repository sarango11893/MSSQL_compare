namespace SqlSuite.Core.Enums;

/// <summary>
/// Classifies a single row difference when the source database is treated as the desired state.
/// </summary>
public enum RowChangeKind
{
    /// <summary>Row exists in source only; it must be inserted into the target.</summary>
    Insert = 0,

    /// <summary>Row exists on both sides but at least one non-key column differs.</summary>
    Update = 1,

    /// <summary>Row exists in target only; it must be deleted from the target.</summary>
    Delete = 2,
}
