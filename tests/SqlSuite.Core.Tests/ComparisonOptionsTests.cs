using SqlSuite.Core.Models;

namespace SqlSuite.Core.Tests;

public sealed class ComparisonOptionsTests
{
    [Fact]
    public void Defaults_AreConservative()
    {
        var options = new ComparisonOptions();

        Assert.False(options.DropObjectsNotInSource);
        Assert.True(options.BlockOnPossibleDataLoss);
        Assert.True(options.UseTransactionForDataScript);
        Assert.True(options.IncludeIdentityInsert);
        Assert.Empty(options.ExcludedTables);
    }

    [Fact]
    public void Clone_CopiesEveryFlag()
    {
        var original = new ComparisonOptions
        {
            DropObjectsNotInSource = true,
            BlockOnPossibleDataLoss = false,
            AllowTableRecreation = false,
            IncludeTransactionalScripts = false,
            ScriptDatabaseOptions = true,
            IgnoreWhitespace = false,
            IgnorePermissions = false,
            IgnoreRoleMembership = false,
            IgnoreColumnCollation = true,
            IgnoreIndexOptions = false,
            GenerateSmartDefaults = false,
            IncludeCompositeObjects = false,
            CommandTimeoutSeconds = 7,
            GenerateDeploymentScript = false,
            IncludeIdentityInsert = false,
            UseTransactionForDataScript = false,
            MaxRowsPerTable = 5,
            SkipNonWritableColumns = false,
            CaseSensitiveDataComparison = false,
            IncludeChangeSummaryComments = false,
        };

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.True(clone.DropObjectsNotInSource);
        Assert.False(clone.BlockOnPossibleDataLoss);
        Assert.False(clone.AllowTableRecreation);
        Assert.False(clone.IncludeTransactionalScripts);
        Assert.True(clone.ScriptDatabaseOptions);
        Assert.False(clone.IgnoreWhitespace);
        Assert.False(clone.IgnorePermissions);
        Assert.False(clone.IgnoreRoleMembership);
        Assert.True(clone.IgnoreColumnCollation);
        Assert.False(clone.IgnoreIndexOptions);
        Assert.False(clone.GenerateSmartDefaults);
        Assert.False(clone.IncludeCompositeObjects);
        Assert.Equal(7, clone.CommandTimeoutSeconds);
        Assert.False(clone.GenerateDeploymentScript);
        Assert.False(clone.IncludeIdentityInsert);
        Assert.False(clone.UseTransactionForDataScript);
        Assert.Equal(5, clone.MaxRowsPerTable);
        Assert.False(clone.SkipNonWritableColumns);
        Assert.False(clone.CaseSensitiveDataComparison);
        Assert.False(clone.IncludeChangeSummaryComments);
    }

    [Fact]
    public void Clone_DeepCopiesExcludedTables()
    {
        var original = new ComparisonOptions();
        original.ExcludedTables.Add("dbo.Audit");

        var clone = original.Clone();
        clone.ExcludedTables.Add("dbo.Legacy");

        Assert.Equal(new[] { "dbo.Audit" }, original.ExcludedTables);
        Assert.Equal(new[] { "dbo.Audit", "dbo.Legacy" }, clone.ExcludedTables);
    }
}
