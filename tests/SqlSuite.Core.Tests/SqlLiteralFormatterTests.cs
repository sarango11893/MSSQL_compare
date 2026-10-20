using SqlSuite.Core.Models;
using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class SqlLiteralFormatterTests
{
    [Fact]
    public void Null_BecomesNullKeyword()
    {
        Assert.Equal("NULL", SqlLiteralFormatter.Format(null));
        Assert.Equal("NULL", SqlLiteralFormatter.Format(DBNull.Value));
    }

    [Fact]
    public void String_EscapesSingleQuotesAndPrefixesUnicode()
    {
        var column = new ColumnSchemaInfo { Name = "Name", DataTypeName = "nvarchar" };

        Assert.Equal("N'O''Brien'", SqlLiteralFormatter.Format("O'Brien", column));
    }

    [Fact]
    public void String_NonUnicodeColumn_HasNoPrefix()
    {
        var column = new ColumnSchemaInfo { Name = "Sku", DataTypeName = "varchar" };

        Assert.Equal("'ABC'", SqlLiteralFormatter.Format("ABC", column));
    }

    [Fact]
    public void EmptyString_IsEmptyLiteral()
    {
        Assert.Equal("N''", SqlLiteralFormatter.FormatString(string.Empty));
        Assert.Equal("''", SqlLiteralFormatter.FormatString(string.Empty, unicode: false));
    }

    [Fact]
    public void Boolean_BecomesBitLiteral()
    {
        Assert.Equal("1", SqlLiteralFormatter.Format(true));
        Assert.Equal("0", SqlLiteralFormatter.Format(false));
    }

    [Fact]
    public void Binary_BecomesHexLiteral()
    {
        Assert.Equal("0x0AFF", SqlLiteralFormatter.Format(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("0x", SqlLiteralFormatter.Format(Array.Empty<byte>()));
    }

    [Fact]
    public void Decimal_UsesInvariantCulture()
    {
        Assert.Equal("1234.56", SqlLiteralFormatter.Format(1234.56m));
    }

    [Fact]
    public void IntegralTypes_RenderWithoutCultureSeparators()
    {
        Assert.Equal("42", SqlLiteralFormatter.Format(42));
        Assert.Equal("42", SqlLiteralFormatter.Format(42L));
        Assert.Equal("42", SqlLiteralFormatter.Format((byte)42));
    }

    [Fact]
    public void DateTime_RendersIsoWithNanoseconds()
    {
        var value = new DateTime(2024, 3, 4, 5, 6, 7, 123, DateTimeKind.Unspecified);

        Assert.Equal("'2024-03-04T05:06:07.1230000'", SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void DateTimeOffset_KeepsOffset()
    {
        var value = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5));

        var literal = SqlLiteralFormatter.Format(value);

        Assert.StartsWith("'2024-03-04T05:06:07.0000000-05:00'", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void Guid_RendersHyphenated()
    {
        var value = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("'0f8fad5b-d9cb-469f-a165-70867728950e'", SqlLiteralFormatter.Format(value));
    }

    [Fact]
    public void TimeSpan_RendersTimeLiteral()
    {
        Assert.Equal("'01:02:03.0000000'", SqlLiteralFormatter.Format(new TimeSpan(1, 2, 3)));
    }

    [Theory]
    [InlineData("Name", "[Name]")]
    [InlineData("weird]name", "[weird]]name]")]
    public void QuoteIdentifier_EscapesClosingBracket(string input, string expected)
    {
        Assert.Equal(expected, SqlLiteralFormatter.QuoteIdentifier(input));
    }
}
