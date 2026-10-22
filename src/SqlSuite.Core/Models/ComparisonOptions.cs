namespace SqlSuite.Core.Models;

/// <summary>
/// Options controlling how scripts are generated and how aggressive the diff is.
/// The defaults are intentionally conservative: nothing is dropped and nothing that
/// could lose data is allowed to run silently.
/// </summary>
public sealed class ComparisonOptions
{
    // ---------------------------------------------------------------- schema

    /// <summary>Emit <c>DROP</c> statements for objects that only exist in the target.</summary>
    public bool DropObjectsNotInSource { get; set; }

    /// <summary>Abort generation when a change could destroy data (for example narrowing a column).</summary>
    public bool BlockOnPossibleDataLoss { get; set; } = true;

    /// <summary>Allow DacFx to rebuild tables (<c>tmp_ms_xx_</c>) when a column cannot be altered in place.</summary>
    public bool AllowTableRecreation { get; set; } = true;

    /// <summary>Wrap the generated script in a transaction.</summary>
    public bool IncludeTransactionalScripts { get; set; } = true;

    /// <summary>Also script database level options.</summary>
    public bool ScriptDatabaseOptions { get; set; }

    /// <summary>Compare ignoring whitespace only differences in programmable objects.</summary>
    public bool IgnoreWhitespace { get; set; } = true;

    /// <summary>Ignore permissions (GRANT / DENY) differences.</summary>
    public bool IgnorePermissions { get; set; } = true;

    /// <summary>Ignore role membership differences.</summary>
    public bool IgnoreRoleMembership { get; set; } = true;

    /// <summary>Ignore column collation differences.</summary>
    public bool IgnoreColumnCollation { get; set; }

    /// <summary>Ignore index options such as fill factor.</summary>
    public bool IgnoreIndexOptions { get; set; } = true;

    /// <summary>Generate default values for new NOT NULL columns so the script can run on populated tables.</summary>
    public bool GenerateSmartDefaults { get; set; } = true;

    /// <summary>Include composite objects (objects referenced by the compared ones).</summary>
    public bool IncludeCompositeObjects { get; set; } = true;

    /// <summary>Command timeout for the deployment / query operations, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>Generate the deployment script during <c>Compare</c> instead of requiring a second pass.</summary>
    public bool GenerateDeploymentScript { get; set; } = true;

    // ------------------------------------------------------------------ data

    /// <summary>Emit <c>SET IDENTITY_INSERT</c> around inserts into tables with identity columns.</summary>
    public bool IncludeIdentityInsert { get; set; } = true;

    /// <summary>Wrap the generated data script in a transaction.</summary>
    public bool UseTransactionForDataScript { get; set; } = true;

    /// <summary>Maximum differences retained per table; the exact comparison may read more rows.</summary>
    public int MaxRowsPerTable { get; set; } = 100_000;

    /// <summary>
    /// Optional DML table allowlist. Bare names match any schema; schema-qualified names match
    /// only that schema and table. An empty list compares every table.
    /// </summary>
    public IList<string> IncludedTables { get; } = new List<string>();

    /// <summary>
    /// Optional per-table row identity overrides. Each mapping must match a non-null unique
    /// key available in both databases; invalid mappings cause that table to be skipped.
    /// </summary>
    public IList<TableKeyMapping> ComparisonKeys { get; } = new List<TableKeyMapping>();

    /// <summary>Include comparable columns only (skip computed, rowversion, generated always).</summary>
    public bool SkipNonWritableColumns { get; set; } = true;

    /// <summary>Tables to skip, matched case-insensitively against <c>schema.table</c>.</summary>
    public IList<string> ExcludedTables { get; } = new List<string>();

    /// <summary>
    /// When <see langword="true"/> (the default) string values are compared exactly, so a casing
    /// difference is reported. When <see langword="false"/> the comparison is case-insensitive,
    /// matching a <c>..._CI_...</c> collation.
    /// </summary>
    public bool CaseSensitiveDataComparison { get; set; } = true;

    /// <summary>Treat CRLF and LF as equivalent when comparing string values.</summary>
    public bool NormalizeLineEndings { get; set; } = true;

    /// <summary>Include a <c>SELECT</c> of the row count in the generated script header.</summary>
    public bool IncludeChangeSummaryComments { get; set; } = true;

    public ComparisonOptions Clone()
    {
        var clone = new ComparisonOptions
        {
            DropObjectsNotInSource = DropObjectsNotInSource,
            BlockOnPossibleDataLoss = BlockOnPossibleDataLoss,
            AllowTableRecreation = AllowTableRecreation,
            IncludeTransactionalScripts = IncludeTransactionalScripts,
            ScriptDatabaseOptions = ScriptDatabaseOptions,
            IgnoreWhitespace = IgnoreWhitespace,
            IgnorePermissions = IgnorePermissions,
            IgnoreRoleMembership = IgnoreRoleMembership,
            IgnoreColumnCollation = IgnoreColumnCollation,
            IgnoreIndexOptions = IgnoreIndexOptions,
            GenerateSmartDefaults = GenerateSmartDefaults,
            IncludeCompositeObjects = IncludeCompositeObjects,
            CommandTimeoutSeconds = CommandTimeoutSeconds,
            GenerateDeploymentScript = GenerateDeploymentScript,
            IncludeIdentityInsert = IncludeIdentityInsert,
            UseTransactionForDataScript = UseTransactionForDataScript,
            MaxRowsPerTable = MaxRowsPerTable,
            SkipNonWritableColumns = SkipNonWritableColumns,
            CaseSensitiveDataComparison = CaseSensitiveDataComparison,
            NormalizeLineEndings = NormalizeLineEndings,
            IncludeChangeSummaryComments = IncludeChangeSummaryComments,
        };

        foreach (var table in ExcludedTables)
            clone.ExcludedTables.Add(table);

        foreach (var table in IncludedTables)
            clone.IncludedTables.Add(table);

        foreach (var mapping in ComparisonKeys)
        {
            clone.ComparisonKeys.Add(new TableKeyMapping
            {
                Table = mapping.Table,
                Columns = mapping.Columns.ToList(),
            });
        }

        return clone;
    }
}
