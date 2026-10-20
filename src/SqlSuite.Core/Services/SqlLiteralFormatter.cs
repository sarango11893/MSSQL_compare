using System.Globalization;
using System.Text;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

/// <summary>
/// Renders CLR values read from <see cref="System.Data.SqlClient.SqlDataReader"/> as T-SQL literals.
/// Everything is produced with the invariant culture so the generated script is machine independent.
/// </summary>
public static class SqlLiteralFormatter
{
    private const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";
    private const string DateTimeOffsetFormat = "yyyy-MM-ddTHH:mm:ss.fffffffzzz";

    public static string Format(object? value) => Format(value, column: null);

    /// <summary>
    /// Formats <paramref name="value"/> for inclusion in a script. <paramref name="column"/> is only
    /// used to decide whether a string literal needs the Unicode <c>N</c> prefix.
    /// </summary>
    public static string Format(object? value, ColumnSchemaInfo? column)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return "NULL";

            case string s:
                return FormatString(s, unicode: column?.IsUnicode ?? true);

            case char c:
                return FormatString(c.ToString(), unicode: column?.IsUnicode ?? true);

            case bool b:
                return b ? "1" : "0";

            case byte[] bytes:
                return FormatBinary(bytes);

            case Guid guid:
                return $"'{guid:D}'";

            case DateTime dateTime:
                return $"'{dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture)}'";

            case DateTimeOffset dateTimeOffset:
                return $"'{dateTimeOffset.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture)}'";

            case TimeSpan timeSpan:
                return $"'{timeSpan:hh\\:mm\\:ss\\.fffffff}'";

            case decimal decimalValue:
                return decimalValue.ToString(CultureInfo.InvariantCulture);

            case double doubleValue:
                return doubleValue.ToString("R", CultureInfo.InvariantCulture);

            case float floatValue:
                return floatValue.ToString("R", CultureInfo.InvariantCulture);

            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";

            default:
                return FormatString(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    unicode: column?.IsUnicode ?? false);
        }
    }

    /// <summary>Wraps and escapes a string literal, doubling any embedded single quote.</summary>
    public static string FormatString(string value, bool unicode = true)
    {
        var escaped = value.Replace("'", "''", StringComparison.Ordinal);
        return unicode ? $"N'{escaped}'" : $"'{escaped}'";
    }

    public static string FormatBinary(byte[] value)
    {
        if (value.Length == 0)
            return "0x";

        var builder = new StringBuilder(2 + (value.Length * 2));
        builder.Append("0x");
        foreach (var b in value)
            builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    /// <summary>Brackets an identifier, escaping any closing bracket.</summary>
    public static string QuoteIdentifier(string name)
        => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
