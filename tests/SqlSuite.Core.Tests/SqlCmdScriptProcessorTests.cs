using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public class SqlCmdScriptProcessorTests
{
    private const string DacScript = """
        GO
        SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS ON;
        GO
        :setvar DatabaseName "Sales"
        :setvar DefaultDataPath "/var/opt/mssql/data/"
        :on error exit
        :setvar __IsSqlCmdEnabled "True"
        IF N'$(__IsSqlCmdEnabled)' NOT LIKE N'True'
            BEGIN
                PRINT N'The SQLCMD mode must be enabled.';
                SET NOEXEC ON;
            END
        GO
        USE [$(DatabaseName)];
        GO
        CREATE TABLE [dbo].[Items] ([Id] INT NOT NULL);
        GO
        """;

    [Fact]
    public void Normalize_RemovesSqlCmdDirectives()
    {
        var result = SqlCmdScriptProcessor.Normalize(DacScript);

        Assert.DoesNotContain(":setvar", result.Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":on error", result.Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_ReplacesVariablesWithTheirValues()
    {
        var result = SqlCmdScriptProcessor.Normalize(DacScript);

        Assert.Contains("USE [Sales];", result.Script);
        Assert.DoesNotContain("$(", result.Script);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Normalize_DatabaseNameArgumentWins()
    {
        var result = SqlCmdScriptProcessor.Normalize(DacScript, "Contoso");

        Assert.Contains("USE [Contoso];", result.Script);
    }

    [Fact]
    public void Normalize_RemovesTheSqlCmdGuardSoTheScriptActuallyRuns()
    {
        var result = SqlCmdScriptProcessor.Normalize(DacScript);

        Assert.DoesNotContain("SET NOEXEC ON", result.Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT LIKE N'True'", result.Script);
        Assert.DoesNotContain("SQLCMD mode must be enabled", result.Script);
    }

    [Fact]
    public void Normalize_ReportsUnknownVariables()
    {
        var result = SqlCmdScriptProcessor.Normalize("SELECT '$(Missing)';");

        Assert.Contains("SELECT '';", result.Script);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Normalize_EmptyScript_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SqlCmdScriptProcessor.Normalize(string.Empty).Script);
        Assert.Equal(string.Empty, SqlCmdScriptProcessor.Normalize(null!).Script);
    }

    [Fact]
    public void SplitBatches_SplitsOnGo()
    {
        var batches = SqlCmdScriptProcessor.SplitBatches("SELECT 1\nGO\nSELECT 2\nGO\n");

        Assert.Equal(new[] { "SELECT 1", "SELECT 2" }, batches);
    }

    [Fact]
    public void SplitBatches_IgnoresGoInsideText()
    {
        var batches = SqlCmdScriptProcessor.SplitBatches("SELECT 'GO';\nGO\n");

        Assert.Single(batches);
        Assert.Equal("SELECT 'GO';", batches[0]);
    }

    [Fact]
    public void SplitBatches_HonoursRepeatCount()
    {
        var batches = SqlCmdScriptProcessor.SplitBatches("SELECT 1\nGO 3\n");

        Assert.Equal(3, batches.Count);
    }

    [Fact]
    public void SplitBatches_EmptyScript_ReturnsNoBatches()
    {
        Assert.Empty(SqlCmdScriptProcessor.SplitBatches(string.Empty));
        Assert.Empty(SqlCmdScriptProcessor.SplitBatches("   \n GO \n  "));
    }

    [Fact]
    public void SplitBatches_NormalizedDacScript_HasNoDirectives()
    {
        var batches = SqlCmdScriptProcessor.SplitBatches(SqlCmdScriptProcessor.Normalize(DacScript).Script);

        Assert.All(batches, batch =>
        {
            Assert.DoesNotContain(":setvar", batch, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$(", batch);
        });
    }
}
