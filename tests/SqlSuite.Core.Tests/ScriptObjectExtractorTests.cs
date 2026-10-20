using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class ScriptObjectExtractorTests
{
    private const string Script = """
        /*
        Script de implementación para SqlSuiteDemo
        */

        GO
        SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;

        SET NUMERIC_ROUNDABORT OFF;

        GO
        USE [SqlSuiteDemo];

        GO
        BEGIN TRANSACTION
        GO
        PRINT N'Quitando Tabla [dbo].[Legacy]...';

        GO
        DROP TABLE [dbo].[Legacy];

        GO
        PRINT N'Quitando Índice [dbo].[Products].[IX_Products_Sku]...';

        GO
        DROP INDEX [IX_Products_Sku]
            ON [dbo].[Products];

        GO
        PRINT N'Modificando Tabla [dbo].[Products]...';

        GO
        ALTER TABLE [dbo].[Products] ADD [Notes] NVARCHAR (100) NULL;

        GO
        PRINT N'Creando Vista [dbo].[vw_ActiveProducts]...';

        GO
        CREATE VIEW [dbo].[vw_ActiveProducts]
            AS SELECT [Id], [Name] FROM [dbo].[Products];

        GO
        PRINT N'Actualización completada.';

        GO
        """;

    [Fact]
    public void Extract_ReturnsWholeScript_WhenNoObjectIsRequested()
    {
        Assert.Equal(Script, ScriptObjectExtractor.Extract(Script, null));
        Assert.Equal(Script, ScriptObjectExtractor.Extract(Script, Array.Empty<string>()));
    }

    [Fact]
    public void Extract_ReturnsEmpty_ForNullOrWhiteSpaceScript()
    {
        Assert.Equal(string.Empty, ScriptObjectExtractor.Extract(null, new[] { "[dbo].[Products]" }));
        Assert.Equal(string.Empty, ScriptObjectExtractor.Extract("   ", new[] { "[dbo].[Products]" }));
    }

    [Fact]
    public void Extract_ReturnsEmpty_WhenNothingMatches()
    {
        var result = ScriptObjectExtractor.Extract(Script, new[] { "[dbo].[Missing]" });

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Extract_KeepsOnlyTheRequestedObjectAndItsChildren()
    {
        var result = ScriptObjectExtractor.Extract(Script, new[] { "[dbo].[Products]" });

        Assert.Contains("IX_Products_Sku", result);
        Assert.Contains("Modificando Tabla [dbo].[Products]", result);
        Assert.DoesNotContain("[dbo].[Legacy]", result);
        Assert.DoesNotContain("vw_ActiveProducts", result);
    }

    [Fact]
    public void Extract_ReproducesThePreambleSoTheSnippetIsRunnable()
    {
        var result = ScriptObjectExtractor.Extract(Script, new[] { "[dbo].[vw_ActiveProducts]" });

        Assert.Contains("USE [SqlSuiteDemo];", result);
        Assert.Contains("SET NUMERIC_ROUNDABORT OFF;", result);
        Assert.StartsWith("/*", result);
    }

    [Fact]
    public void Extract_SupportsSeveralObjectsAndDeduplicatesNames()
    {
        var result = ScriptObjectExtractor.Extract(
            Script,
            new[] { "[dbo].[Legacy]", "[dbo].[Legacy]", "[dbo].[vw_ActiveProducts]" });

        Assert.Contains("DROP TABLE [dbo].[Legacy];", result);
        Assert.Contains("CREATE VIEW [dbo].[vw_ActiveProducts]", result);
        Assert.DoesNotContain("ALTER TABLE [dbo].[Products]", result);
    }

    [Fact]
    public void Extract_DoesNotMatchAnObjectWhoseNameIsAPrefixOfAnother()
    {
        // "[dbo].[Product]" must not swallow the blocks of "[dbo].[Products]".
        const string script = """
            PRINT N'Modificando Tabla [dbo].[Products]...';

            GO
            ALTER TABLE [dbo].[Products] ADD [Notes] NVARCHAR (100) NULL;

            GO
            """;

        Assert.Equal(string.Empty, ScriptObjectExtractor.Extract(script, new[] { "[dbo].[Product]" }));
        Assert.Contains("ADD [Notes]", ScriptObjectExtractor.Extract(script, new[] { "[dbo].[Products]" }));
    }

    [Fact]
    public void Extract_ReturnsEmpty_WhenTheScriptHasNoHeaders()
    {
        Assert.Equal(string.Empty, ScriptObjectExtractor.Extract("SELECT 1;", new[] { "[dbo].[Products]" }));
    }

    private const string RemovableScript = """
        /*
        Script de implementación para SqlSuiteDemo
        */

        GO
        SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;

        SET NUMERIC_ROUNDABORT OFF;

        GO
        USE [SqlSuiteDemo];

        GO
        CREATE TABLE #tmpErrors (Error int)
        GO
        SET XACT_ABORT ON
        GO
        BEGIN TRANSACTION
        GO
        PRINT N'Quitando Tabla [dbo].[Legacy]...';

        GO
        DROP TABLE [dbo].[Legacy];

        GO
        PRINT N'Creando Restricción CHECK [dbo].[CK_Products_Price]...';

        GO
        ALTER TABLE [dbo].[Products] WITH CHECK ADD CONSTRAINT [CK_Products_Price] CHECK ([Price]>(0));

        GO
        PRINT N'Modificando Procedimiento [dbo].[usp_NoChanges]...';

        GO
        ALTER PROCEDURE [dbo].[usp_NoChanges] AS SELECT 1;

        GO
        IF EXISTS (SELECT * FROM #tmpErrors) ROLLBACK TRANSACTION
        GO
        IF @@TRANCOUNT>0 BEGIN
        PRINT N'La parte de transacción de la actualización de la base de datos se realizó correctamente.'
        COMMIT TRANSACTION
        END
        GO
        PRINT N'Actualización completada.';

        GO
        """;

    private const string FilterableScript = """
        USE [SqlSuiteDemo];
        GO
        CREATE TABLE #tmpErrors (Error int);
        GO
        BEGIN TRANSACTION;
        GO
        /* Se está quitando la tabla [dbo].[Unselected]; se podrían perder datos. */
        IF EXISTS (SELECT 1 FROM [dbo].[Unselected])
            RAISERROR (N'Hay filas.', 16, 127) WITH NOWAIT
        GO
        /* Se está quitando la tabla [dbo].[SelectedTable]; se podrían perder datos. */
        IF EXISTS (SELECT 1 FROM [dbo].[SelectedTable])
            RAISERROR (N'Hay filas.', 16, 127) WITH NOWAIT
        GO
        PRINT N'Modificando Tabla [dbo].[SelectedTable]...';
        GO
        ALTER TABLE [dbo].[SelectedTable] ADD [SelectedColumn] int NULL;
        GO
        PRINT N'Quitando Procedimiento [dbo].[SelectedDrop]...';
        GO
        DROP PROCEDURE [dbo].[SelectedDrop];
        GO
        PRINT N'Modificando Procedimiento [dbo].[SelectedProc]...';
        GO
        ALTER PROCEDURE [dbo].[SelectedProc] AS SELECT 1;
        GO
        PRINT N'Actualizando Procedimiento [dbo].[UnselectedProc]...';
        GO
        EXECUTE sp_refreshsqlmodule N'[dbo].[UnselectedProc]';
        GO
        IF EXISTS (SELECT * FROM #tmpErrors) ROLLBACK TRANSACTION
        GO
        PRINT N'La parte de transacción de la actualización se realizó correctamente.';
        GO
        IF @@TRANCOUNT > 0 COMMIT TRANSACTION
        GO
        PRINT N'Comprobando los datos existentes con las restricciones recién creadas';
        GO
        ALTER TABLE [dbo].[SelectedTable] WITH CHECK CHECK CONSTRAINT [CK_SelectedTable];
        GO
        PRINT N'Actualización completada.';
        GO
        """;

    [Fact]
    public void Remove_ReturnsScriptUnchanged_WhenNothingIsExcluded()
    {
        Assert.Equal(RemovableScript, ScriptObjectExtractor.Remove(RemovableScript, null));
        Assert.Equal(RemovableScript, ScriptObjectExtractor.Remove(RemovableScript, Array.Empty<string>()));
    }

    [Fact]
    public void Remove_ReturnsEmpty_ForNullOrWhiteSpaceScript()
    {
        Assert.Equal(string.Empty, ScriptObjectExtractor.Remove(null, new[] { "[dbo].[Products]" }));
        Assert.Equal(string.Empty, ScriptObjectExtractor.Remove("   ", new[] { "[dbo].[Products]" }));
    }

    [Fact]
    public void Remove_ReturnsScriptUnchanged_WhenTheScriptHasNoHeaders()
    {
        Assert.Equal("SELECT 1;", ScriptObjectExtractor.Remove("SELECT 1;", new[] { "[dbo].[Products]" }));
    }

    [Fact]
    public void Remove_DropsOnlyTheBlocksOfTheExcludedObject()
    {
        var result = ScriptObjectExtractor.Remove(RemovableScript, new[] { "[dbo].[Legacy]" });

        Assert.DoesNotContain("Legacy", result);
        Assert.Contains("CK_Products_Price", result);
        Assert.Contains("usp_NoChanges", result);
    }

    [Fact]
    public void Remove_KeepsTheTransactionBookkeepingTheTrailerNeeds()
    {
        var result = ScriptObjectExtractor.Remove(RemovableScript, new[] { "[dbo].[Legacy]" });

        Assert.Contains("CREATE TABLE #tmpErrors", result);
        Assert.Contains("BEGIN TRANSACTION", result);
        Assert.Contains("COMMIT TRANSACTION", result);
        Assert.Contains("USE [SqlSuiteDemo];", result);
    }

    [Fact]
    public void Remove_DropsChildObjectBlocks_WhenTheParentIsExcluded()
    {
        // Excluding a table has to drop the blocks DacFx emits under the child object's name.
        var result = ScriptObjectExtractor.Remove(
            RemovableScript,
            new[] { "[dbo].[Products]", "[dbo].[CK_Products_Price]" });

        Assert.DoesNotContain("CK_Products_Price", result);
        Assert.DoesNotContain("ADD CONSTRAINT", result);
        Assert.Contains("usp_NoChanges", result);
    }

    [Fact]
    public void Remove_KeepsTheTrailer_WhenTheBlockOwningItIsExcluded()
    {
        // DacFx appends the transaction epilogue to the last object it scripts, so excluding that
        // object must not delete the ROLLBACK/COMMIT pair.
        var result = ScriptObjectExtractor.Remove(RemovableScript, new[] { "[dbo].[usp_NoChanges]" });

        Assert.DoesNotContain("usp_NoChanges", result);
        Assert.Contains("ROLLBACK TRANSACTION", result);
        Assert.Contains("COMMIT TRANSACTION", result);
        Assert.Contains("Actualización completada.", result);
    }

    [Fact]
    public void Remove_ReturnsEmpty_WhenEveryObjectIsExcluded()
    {
        const string script = """
            PRINT N'Quitando Tabla [dbo].[Legacy]...';

            GO
            DROP TABLE [dbo].[Legacy];

            GO
            """;

        Assert.Equal(string.Empty, ScriptObjectExtractor.Remove(script, new[] { "[dbo].[Legacy]" }));
    }

    [Fact]
    public void Remove_DoesNotMatchAnObjectWhoseNameIsAPrefixOfAnother()
    {
        var result = ScriptObjectExtractor.Remove(RemovableScript, new[] { "[dbo].[Legacy]" });

        Assert.Contains("CK_Products_Price", result);
        Assert.DoesNotContain("DROP TABLE [dbo].[Legacy]", result);
    }

    [Fact]
    public void KeepOnly_IncludesSelectedObjectsAndOmitsUnselectedOperationsAndGuards()
    {
        var result = ScriptObjectExtractor.KeepOnly(
            FilterableScript,
            new[] { "[dbo].[SelectedProc]", "[dbo].[SelectedDrop]" });

        Assert.Contains("DROP PROCEDURE [dbo].[SelectedDrop]", result);
        Assert.Contains("ALTER PROCEDURE [dbo].[SelectedProc]", result);
        Assert.DoesNotContain("[dbo].[Unselected]", result);
        Assert.DoesNotContain("UnselectedProc", result);
        Assert.DoesNotContain("SelectedTable", result);
        Assert.Contains("CREATE TABLE #tmpErrors", result);
        Assert.Contains("BEGIN TRANSACTION", result);
        Assert.Contains("COMMIT TRANSACTION", result);
        Assert.Contains("Actualización completada.", result);
    }

    [Fact]
    public void KeepOnly_PreservesOnlyTheSelectedObjectsDataLossGuardAndConstraintValidation()
    {
        var result = ScriptObjectExtractor.KeepOnly(
            FilterableScript,
            new[] { "[dbo].[SelectedTable]" });

        Assert.Contains("IF EXISTS (SELECT 1 FROM [dbo].[SelectedTable])", result);
        Assert.DoesNotContain("[dbo].[Unselected]", result);
        Assert.Contains("ALTER TABLE [dbo].[SelectedTable] WITH CHECK CHECK CONSTRAINT", result);
        Assert.DoesNotContain("SelectedProc", result);
        Assert.DoesNotContain("SelectedDrop", result);
        Assert.DoesNotContain("UnselectedProc", result);
    }

    [Fact]
    public void KeepOnly_ReturnsEmpty_WhenNoObjectsAreSelected()
    {
        Assert.Equal(string.Empty, ScriptObjectExtractor.KeepOnly(FilterableScript, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Quitando Tabla [dbo].[Products]...", "[dbo].[Products]", true)]
    [InlineData("Creando Índice [dbo].[Products].[IX_Sku]...", "[dbo].[Products]", true)]
    [InlineData("Creando Vista [dbo].[Products]...", "[dbo].[Products]", true)]
    [InlineData("Modificando Tabla [dbo].[ProductsArchive]...", "[dbo].[Products]", false)]
    [InlineData("Modificando Tabla [dbo].[Orders]...", "[dbo].[Products]", false)]
    public void HeaderMentionsObject_MatchesTheObjectOrItsChildren(string header, string name, bool expected)
        => Assert.Equal(expected, ScriptObjectExtractor.HeaderMentionsObject(header, name));
}
