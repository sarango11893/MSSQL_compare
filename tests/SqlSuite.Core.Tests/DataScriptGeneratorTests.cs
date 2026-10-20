using SqlSuite.Core.Enums;
using SqlSuite.Core.Models;
using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class DataScriptGeneratorTests
{
    private static TableSchemaInfo CreateTable(bool hasIdentity = false) => new()
    {
        SchemaName = "dbo",
        TableName = "Products",
        KeySource = "Primary key",
        Columns = new ColumnSchemaInfo[]
        {
            new() { Name = "Id", DataTypeName = "int", IsIdentity = hasIdentity },
            new() { Name = "Name", DataTypeName = "nvarchar", IsNullable = true },
        },
        KeyColumns = new[] { "Id" },
    };

    private static RowChange CreateInsert(TableSchemaInfo table, int id, string name) => new()
    {
        Table = table,
        Kind = RowChangeKind.Insert,
        KeyValues = new object?[] { id },
        SourceValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["Name"] = name,
        },
        KeyDisplay = $"Id={id}",
    };

    private static RowChange CreateUpdate(TableSchemaInfo table, int id, string oldName, string newName) => new()
    {
        Table = table,
        Kind = RowChangeKind.Update,
        KeyValues = new object?[] { id },
        SourceValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["Name"] = newName,
        },
        TargetValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["Name"] = oldName,
        },
        ChangedColumns = new[] { new ColumnValueChange("Name", newName, oldName) },
        KeyDisplay = $"Id={id}",
    };

    private static RowChange CreateDelete(TableSchemaInfo table, int id) => new()
    {
        Table = table,
        Kind = RowChangeKind.Delete,
        KeyValues = new object?[] { id },
        TargetValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Id"] = id },
        KeyDisplay = $"Id={id}",
    };

    [Fact]
    public void Generate_WithNoChanges_ReturnsEmptyScript()
    {
        var generator = new DataScriptGenerator(new ComparisonOptions());

        Assert.Equal(string.Empty, generator.Generate(Array.Empty<TableDataDifference>()));

        var table = CreateTable();
        var empty = new TableDataDifference { Table = table };
        Assert.Equal(string.Empty, generator.Generate(new[] { empty }));
    }

    [Fact]
    public void Generate_SkipsSkippedTables()
    {
        var table = CreateTable();
        var skipped = new TableDataDifference { Table = table, SkipReason = "no key" };

        var script = new DataScriptGenerator(new ComparisonOptions()).Generate(new[] { skipped });

        Assert.Equal(string.Empty, script);
    }

    [Fact]
    public void Generate_OrdersDeletesThenUpdatesThenInserts()
    {
        var table = CreateTable();
        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[]
            {
                CreateInsert(table, 1, "new"),
                CreateUpdate(table, 2, "old", "updated"),
                CreateDelete(table, 3),
            },
        };

        var script = new DataScriptGenerator(new ComparisonOptions()).Generate(new[] { diff });

        var deleteIndex = script.IndexOf("DELETE FROM", StringComparison.Ordinal);
        var updateIndex = script.IndexOf("UPDATE ", StringComparison.Ordinal);
        var insertIndex = script.IndexOf("INSERT INTO", StringComparison.Ordinal);

        Assert.True(deleteIndex >= 0, "the script should contain a DELETE");
        Assert.True(updateIndex > deleteIndex, "UPDATE must come after DELETE");
        Assert.True(insertIndex > updateIndex, "INSERT must come after UPDATE");
    }

    [Fact]
    public void Generate_Update_OnlySetsChangedColumns()
    {
        var table = CreateTable();
        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[] { CreateUpdate(table, 7, "viejo", "nuevo") },
        };

        var script = new DataScriptGenerator(new ComparisonOptions()).Generate(new[] { diff });

        Assert.Contains("[Name] = N'nuevo'", script, StringComparison.Ordinal);
        Assert.Contains("WHERE [Id] = 7", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[Id] = 7 SET", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Insert_UsesIdentityInsertWhenTableHasIdentity()
    {
        var table = CreateTable(hasIdentity: true);
        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[] { CreateInsert(table, 5, "uno") },
        };

        var script = new DataScriptGenerator(new ComparisonOptions()).Generate(new[] { diff });

        Assert.Contains("SET IDENTITY_INSERT [dbo].[Products] ON;", script, StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO [dbo].[Products] ([Id], [Name]) VALUES (5, N'uno');",
            script,
            StringComparison.Ordinal);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Products] OFF;", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("SET IDENTITY_INSERT [dbo].[Products] ON;", StringComparison.Ordinal)
                < script.IndexOf("INSERT INTO [dbo].[Products]", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("SET IDENTITY_INSERT [dbo].[Products] OFF;", StringComparison.Ordinal)
                > script.IndexOf("INSERT INTO [dbo].[Products]", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_Insert_OmitsIdentityColumnWhenDisabled()
    {
        var table = CreateTable(hasIdentity: true);
        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[] { CreateInsert(table, 5, "uno") },
        };

        var options = new ComparisonOptions { IncludeIdentityInsert = false };
        var script = new DataScriptGenerator(options).Generate(new[] { diff });

        Assert.DoesNotContain("IDENTITY_INSERT", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [dbo].[Products] ([Name]) VALUES (N'uno');", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WrapsInTransactionByDefault()
    {
        var table = CreateTable();
        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[] { CreateInsert(table, 1, "x") },
        };

        var script = new DataScriptGenerator(new ComparisonOptions()).Generate(new[] { diff });

        Assert.Contains("BEGIN TRANSACTION;", script, StringComparison.Ordinal);
        Assert.Contains("COMMIT TRANSACTION;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithoutTransaction_DoesNotEmitBeginOrCommit()
    {
        var table = CreateTable();
        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[] { CreateInsert(table, 1, "x") },
        };

        var options = new ComparisonOptions { UseTransactionForDataScript = false };
        var script = new DataScriptGenerator(options).Generate(new[] { diff });

        Assert.DoesNotContain("BEGIN TRANSACTION", script, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMIT TRANSACTION", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CompositeKey_PredicateUsesAllKeyColumns()
    {
        var table = new TableSchemaInfo
        {
            SchemaName = "dbo",
            TableName = "OrderLines",
            KeySource = "Primary key",
            Columns = new ColumnSchemaInfo[]
            {
                new() { Name = "OrderId", DataTypeName = "int" },
                new() { Name = "LineNumber", DataTypeName = "int" },
            },
            KeyColumns = new[] { "OrderId", "LineNumber" },
        };

        var diff = new TableDataDifference
        {
            Table = table,
            Changes = new[]
            {
                new RowChange
                {
                    Table = table,
                    Kind = RowChangeKind.Delete,
                    KeyValues = new object?[] { 1, 2 },
                },
            },
        };

        var script = new DataScriptGenerator(new ComparisonOptions()).Generate(new[] { diff });

        Assert.Contains("WHERE [OrderId] = 1 AND [LineNumber] = 2;", script, StringComparison.Ordinal);
    }
}
