using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class DacModelReaderTests
{
    [Fact]
    public void ReadTables_AssociatesKeysWithSchemaQualifiedTableNames()
    {
        using var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
        model.AddObjects("CREATE SCHEMA [sales];");
        model.AddObjects("CREATE SCHEMA [audit];");
        model.AddObjects("CREATE TABLE [sales].[Shared] ([SalesId] int NOT NULL PRIMARY KEY);");
        model.AddObjects("CREATE TABLE [audit].[Shared] ([AuditId] int NOT NULL PRIMARY KEY);");

        var tables = DacModelReader.ReadTables(model)
            .ToDictionary(table => table.FullName, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { "SalesId" }, tables["sales.Shared"].KeyColumns);
        Assert.Equal(new[] { "AuditId" }, tables["audit.Shared"].KeyColumns);
    }
}
