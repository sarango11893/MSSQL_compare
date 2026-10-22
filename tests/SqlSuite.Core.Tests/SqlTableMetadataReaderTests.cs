using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class SqlTableMetadataReaderTests
{
    [Fact]
    public void BuildTableFilter_UsesParametersForQualifiedAndUnqualifiedTables()
    {
        var filter = SqlTableMetadataReader.BuildTableFilter(
            "s",
            new[] { "dbo.Orders", "Customers" },
            out var parameters);

        Assert.Contains("s.name COLLATE Latin1_General_100_CI_AS = @schema0", filter);
        Assert.Contains("t.name COLLATE Latin1_General_100_CI_AS = @table0", filter);
        Assert.Contains("t.name COLLATE Latin1_General_100_CI_AS = @table1", filter);
        Assert.Equal(
            new[] { ("@schema0", "dbo"), ("@table0", "Orders"), ("@table1", "Customers") },
            parameters);
    }

    [Fact]
    public void ComparisonKeyOverride_IsCopiedWhenOptionsAreCloned()
    {
        var options = new SqlSuite.Core.Models.ComparisonOptions();
        options.ComparisonKeys.Add(new SqlSuite.Core.Models.TableKeyMapping
        {
            Table = "dbo.SYS_EsquemaXML_archivo",
            Columns = new List<string> { "idesquema", "nombre" },
        });

        var clone = options.Clone();
        clone.ComparisonKeys[0].Columns.Add("extra");

        Assert.Equal(new[] { "idesquema", "nombre" }, options.ComparisonKeys[0].Columns);
        Assert.Equal(new[] { "idesquema", "nombre", "extra" }, clone.ComparisonKeys[0].Columns);
    }

    [Fact]
    public void BuildTableFilter_LeavesEmptyAllowlistUnfiltered()
    {
        var filter = SqlTableMetadataReader.BuildTableFilter("s", Array.Empty<string>(), out var parameters);

        Assert.Empty(filter);
        Assert.Empty(parameters);
    }
}
