using SqlSuite.Core.Models;
using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class DataComparerValueTests
{
    private static readonly ComparisonOptions CaseSensitive = new()
    {
        CaseSensitiveDataComparison = true,
        NormalizeLineEndings = false,
    };
    private static readonly ComparisonOptions CaseInsensitive = new()
    {
        CaseSensitiveDataComparison = false,
        NormalizeLineEndings = false,
    };

    [Fact]
    public void BothNull_AreEqual()
    {
        Assert.Equal(0, DataComparerService.CompareValues(null, DBNull.Value, CaseSensitive));
    }

    [Fact]
    public void NullSortsBeforeValue()
    {
        Assert.True(DataComparerService.CompareValues(null, 1, CaseSensitive) < 0);
        Assert.True(DataComparerService.CompareValues(1, null, CaseSensitive) > 0);
    }

    [Fact]
    public void MixedNumericTypes_CompareByValue()
    {
        Assert.Equal(0, DataComparerService.CompareValues(1, 1L, CaseSensitive));
        Assert.Equal(0, DataComparerService.CompareValues(1.0m, 1, CaseSensitive));
        Assert.True(DataComparerService.CompareValues(1.5m, 2, CaseSensitive) < 0);
    }

    [Fact]
    public void Strings_HonourCaseSensitivityOption()
    {
        Assert.NotEqual(0, DataComparerService.CompareValues("abc", "ABC", CaseSensitive));
        Assert.Equal(0, DataComparerService.CompareValues("abc", "ABC", CaseInsensitive));
    }

    [Fact]
    public void Strings_CanTreatCrLfAndLfAsEquivalent()
    {
        const string windowsLineEndings = "line one\r\nline two\r\nline three";
        const string unixLineEndings = "line one\nline two\nline three";

        Assert.NotEqual(0, DataComparerService.CompareValues(windowsLineEndings, unixLineEndings, CaseSensitive));
        Assert.Equal(0, DataComparerService.CompareValues(
            windowsLineEndings,
            unixLineEndings,
            CaseSensitive,
            normalizeLineEndings: true));
    }

    [Fact]
    public void StringLineEndingNormalization_DoesNotIgnoreOtherDifferences()
    {
        Assert.NotEqual(0, DataComparerService.CompareValues(
            "line one\r\n",
            "line one \n",
            CaseSensitive,
            normalizeLineEndings: true));
        Assert.NotEqual(0, DataComparerService.CompareValues(
            "line one\r",
            "line one\n",
            CaseSensitive,
            normalizeLineEndings: true));
    }

    [Fact]
    public void ServerSideStringComparison_UsesBinaryCollationWithoutMaxConversions()
    {
        var column = new ColumnSchemaInfo { Name = "Body", DataTypeName = "nvarchar" };

        var equality = DataComparerService.BuildSqlEquality(
            "s.[Body]",
            "t.[Body]",
            column);
        var difference = DataComparerService.BuildSqlDifference(
            "s.[Body]",
            "t.[Body]",
            column);

        Assert.Contains("s.[Body] COLLATE Latin1_General_100_BIN2", equality);
        Assert.Contains("t.[Body] COLLATE Latin1_General_100_BIN2", equality);
        Assert.Contains("s.[Body] COLLATE Latin1_General_100_BIN2", difference);
        Assert.Contains("t.[Body] COLLATE Latin1_General_100_BIN2", difference);
        Assert.DoesNotContain("nvarchar(max)", difference);
        Assert.DoesNotContain("varbinary(max)", difference);
    }

    [Fact]
    public void ServerSideStringComparison_AllowsStringCandidatesForClientSideFiltering()
    {
        var column = new ColumnSchemaInfo { Name = "Body", DataTypeName = "nvarchar" };

        Assert.True(DataComparerService.IsSqlComparableType(column));
    }

    [Fact]
    public void Binary_ComparesLexicographically()
    {
        Assert.Equal(0, DataComparerService.CompareValues(new byte[] { 1, 2 }, new byte[] { 1, 2 }, CaseSensitive));
        Assert.True(DataComparerService.CompareValues(new byte[] { 1, 2 }, new byte[] { 1, 3 }, CaseSensitive) < 0);
        Assert.True(DataComparerService.CompareValues(new byte[] { 1, 2 }, new byte[] { 1, 2, 0 }, CaseSensitive) < 0);
    }

    [Fact]
    public void SameType_UsesIComparable()
    {
        var earlier = new DateTime(2024, 1, 1);
        var later = new DateTime(2024, 1, 2);

        Assert.True(DataComparerService.CompareValues(earlier, later, CaseSensitive) < 0);
    }

    [Fact]
    public void DifferentTypes_FallBackToOrdinalText()
    {
        Assert.Equal(0, DataComparerService.CompareValues(Guid.Empty, Guid.Empty, CaseSensitive));
        Assert.NotEqual(0, DataComparerService.CompareValues(true, false, CaseSensitive));
    }

    [Fact]
    public void Booleans_CompareAsComparable()
    {
        Assert.Equal(0, DataComparerService.CompareValues(true, true, CaseSensitive));
        Assert.NotEqual(0, DataComparerService.CompareValues(true, false, CaseSensitive));
    }

    [Fact]
    public void Guid_ComparesByComparable()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");

        Assert.True(DataComparerService.CompareValues(first, second, CaseSensitive) < 0);
    }
}
