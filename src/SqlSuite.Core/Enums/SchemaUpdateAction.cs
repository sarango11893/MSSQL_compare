namespace SqlSuite.Core.Enums;

/// <summary>
/// Normalized form of <c>Microsoft.SqlServer.Dac.Compare.SchemaUpdateAction</c>,
/// expressed from the point of view of the target database.
/// </summary>
public enum SchemaUpdateAction
{
    /// <summary>The object exists in the source but not in the target.</summary>
    Create = 0,

    /// <summary>The object exists on both sides but differs.</summary>
    Alter = 1,

    /// <summary>The object exists in the target but not in the source.</summary>
    Drop = 2,
}
