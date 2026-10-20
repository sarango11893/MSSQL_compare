using System.Text;
using System.Text.RegularExpressions;

namespace SqlSuite.Core.Services;

/// <summary>
/// Extracts the part of a normalized DacFx deployment script that belongs to one or more objects.
/// <para>
/// DacFx writes deployment scripts as a sequence of <c>PRINT N'...'</c> headers, each one followed by
/// the batches that affect that object (<c>Quitando Tabla [dbo].[Legacy]...</c>,
/// <c>Creando Índice [dbo].[Customers].[IX_Customers_Name]...</c>, ...). Slicing those headers is
/// instantaneous, while asking DacFx for a per-object script costs a full regeneration (~4s).
/// </para>
/// </summary>
public static class ScriptObjectExtractor
{
    private static readonly Regex HeaderRegex = new(
        @"^[ \t]*PRINT[ \t]+N'(?<text>[^']*)'",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Marks the transaction epilogue that DacFx appends to the last object it scripts.
    private static readonly Regex TrailerRegex = new(
        @"^[ \t]*IF[ \t]+EXISTS[ \t]*\([ \t]*SELECT[ \t]+\*[ \t]+FROM[ \t]+#tmpErrors[ \t]*\)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // A sliced script has no transaction wrapper, so it must at least run in the right database
    // with the SET options the deployment needs (QUOTED_IDENTIFIER and friends).
    private static readonly Regex PreambleLineRegex = new(
        @"^[ \t]*(?:USE[ \t]+\[[^\]\r\n]+\][ \t]*;?|SET[ \t]+ANSI_[^\r\n]*|SET[ \t]+NUMERIC_ROUNDABORT[^\r\n]*)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex BatchSeparatorRegex = new(
        @"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the blocks of <paramref name="script"/> that affect any of the given objects,
    /// prefixed with the <c>USE</c>/<c>SET</c> lines taken from the script preamble.
    /// </summary>
    /// <param name="script">A script already normalized by <see cref="SqlCmdScriptProcessor.Normalize"/>.</param>
    /// <param name="objectNames">Fully qualified names such as <c>[dbo].[Products]</c>.</param>
    /// <returns>
    /// An empty string when no header mentions the requested objects; <paramref name="script"/>
    /// itself when no object names are supplied.
    /// </returns>
    public static string Extract(string? script, IEnumerable<string>? objectNames)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var names = NormalizeNames(objectNames);

        if (names.Count == 0)
            return script;

        var headers = HeaderRegex.Matches(script);
        if (headers.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("/* Extracto del script de implementación generado para ")
               .AppendJoin(", ", names)
               .AppendLine(". */")
               .AppendLine();

        foreach (Match match in PreambleLineRegex.Matches(script[..headers[0].Index]))
            builder.Append(match.Value.Trim()).AppendLine().AppendLine("GO").AppendLine();

        var matchedBlocks = 0;

        for (var index = 0; index < headers.Count; index++)
        {
            var headerText = headers[index].Groups["text"].Value;

            if (!names.Any(name => HeaderMentionsObject(headerText, name)))
                continue;

            var start = headers[index].Index;
            var end = index + 1 < headers.Count ? headers[index + 1].Index : script.Length;

            builder.Append(script.AsSpan(start, end - start).TrimEnd())
                   .AppendLine()
                   .AppendLine();

            matchedBlocks++;
        }

        return matchedBlocks == 0 ? string.Empty : builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Returns <paramref name="script"/> without the blocks that belong to any of the given objects.
    /// <para>
    /// This is the mechanism behind the selection checkboxes of the schema tree: DacFx cannot reliably
    /// re-generate a script for a subset of the differences (<c>Exclude</c> fails or corrupts the
    /// baseline when the object has dependencies), so the full script is generated once and every
    /// unchecked object is sliced out of it.
    /// </para>
    /// </summary>
    /// <param name="script">A script already normalized by <see cref="SqlCmdScriptProcessor.Normalize"/>.</param>
    /// <param name="objectNames">Fully qualified names such as <c>[dbo].[Products]</c>.</param>
    /// <returns>
    /// <paramref name="script"/> unchanged when no object names are supplied or when none of them has
    /// its own block, and an empty string when every block belongs to an excluded object.
    /// </returns>
    public static string Remove(string? script, IEnumerable<string>? objectNames)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var names = NormalizeNames(objectNames);
        if (names.Count == 0)
            return script;

        var headers = HeaderRegex.Matches(script);
        if (headers.Count == 0)
            return script;

        // DacFx closes the deployment transaction inside the block of the last object it touches, so
        // that fragment must be preserved even when the object that owns it is excluded.
        var trailer = TrailerRegex.Match(script);
        var trailerIndex = trailer.Success ? trailer.Index : -1;

        var builder = new StringBuilder(script.Length);

        // The lines before the first header create the transaction bookkeeping table and open the
        // transaction that the trailer closes, so they always survive the filtering.
        builder.Append(script.AsSpan(0, headers[0].Index));

        var keptBlocks = 0;

        for (var index = 0; index < headers.Count; index++)
        {
            var headerText = headers[index].Groups["text"].Value;
            var start = headers[index].Index;
            var end = index + 1 < headers.Count ? headers[index + 1].Index : script.Length;

            if (!names.Any(name => HeaderMentionsObject(headerText, name)))
            {
                builder.Append(script.AsSpan(start, end - start));
                keptBlocks++;
                continue;
            }

            if (trailerIndex >= start && trailerIndex < end)
            {
                builder.Append(script.AsSpan(trailerIndex, end - trailerIndex));
                keptBlocks++;
            }
        }

        if (keptBlocks == 0)
            return string.Empty;

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Returns only the DacFx object blocks that belong to the selected objects, plus the
    /// transaction scaffolding and selected-object validation guards required to run them.
    /// </summary>
    /// <remarks>
    /// A deployment script can contain dependency refreshes and data-loss checks for objects that
    /// are not present in the comparison tree. Removing only known excluded names therefore leaks
    /// unrelated operations into partial scripts. This method uses an allow-list instead.
    /// </remarks>
    public static string KeepOnly(string? script, IEnumerable<string>? objectNames)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        var names = NormalizeNames(objectNames);
        if (names.Count == 0)
            return string.Empty;

        var headers = HeaderRegex.Matches(script);
        if (headers.Count == 0)
            return string.Empty;

        var hasSelectedObjectBlock = headers
            .Cast<Match>()
            .Any(header => HeaderMentionsAny(header.Groups["text"].Value, names));

        if (!hasSelectedObjectBlock)
            return string.Empty;

        var builder = new StringBuilder(script.Length);
        AppendFilteredPreamble(builder, script[..headers[0].Index], names);

        var trailer = TrailerRegex.Match(script);

        for (var index = 0; index < headers.Count; index++)
        {
            var headerText = headers[index].Groups["text"].Value;
            var start = headers[index].Index;
            var end = index + 1 < headers.Count ? headers[index + 1].Index : script.Length;
            var block = script.AsSpan(start, end - start);

            if (trailer.Success && start >= trailer.Index
                && !IsConstraintValidationHeader(headerText)
                && !IsCompletionHeader(headerText))
            {
                builder.Append(block);
                continue;
            }

            if (HeaderMentionsAny(headerText, names)
                || IsConstraintValidationHeader(headerText) && ContainsAnyObjectReference(block, names)
                || IsCompletionHeader(headerText))
            {
                builder.Append(block);
                continue;
            }

            // The DacFx transaction epilogue is appended to whichever object happens to be last
            // in its output. Preserve its first batch without keeping that unrelated object's SQL.
            if (trailer.Success && trailer.Index >= start && trailer.Index < end)
                builder.Append(script.AsSpan(trailer.Index, end - trailer.Index));
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendFilteredPreamble(
        StringBuilder builder,
        string preamble,
        IReadOnlyList<string> selectedNames)
    {
        var position = 0;

        foreach (Match separator in BatchSeparatorRegex.Matches(preamble))
        {
            var batch = preamble[position..separator.Index];
            if (!IsUnselectedDataLossGuard(batch, selectedNames))
                builder.Append(batch).Append(separator.Value).AppendLine();

            position = separator.Index + separator.Length;
        }

        var finalBatch = preamble[position..];
        if (!IsUnselectedDataLossGuard(finalBatch, selectedNames))
            builder.Append(finalBatch);
    }

    private static bool IsUnselectedDataLossGuard(string batch, IReadOnlyList<string> selectedNames)
        => batch.Contains("RAISERROR", StringComparison.OrdinalIgnoreCase)
            && batch.Contains("IF EXISTS", StringComparison.OrdinalIgnoreCase)
            && !batch.Contains("#tmpErrors", StringComparison.OrdinalIgnoreCase)
            && !ContainsAnyObjectReference(batch.AsSpan(), selectedNames);

    private static bool HeaderMentionsAny(string header, IReadOnlyList<string> names)
        => names.Any(name => HeaderMentionsObject(header, name));

    private static bool ContainsAnyObjectReference(ReadOnlySpan<char> text, IReadOnlyList<string> names)
    {
        var value = text.ToString();
        return names.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConstraintValidationHeader(string header)
        => header.Contains("comprobando", StringComparison.OrdinalIgnoreCase)
            && header.Contains("restriccion", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletionHeader(string header)
        => header.Contains("actualización completada", StringComparison.OrdinalIgnoreCase)
            || header.Contains("actualizacion completada", StringComparison.OrdinalIgnoreCase);

    private static List<string> NormalizeNames(IEnumerable<string>? objectNames)
        => (objectNames ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// <see langword="true"/> when <paramref name="headerText"/> names the object itself or one of
    /// its children, for example <c>[dbo].[Customers]</c> inside <c>[dbo].[Customers].[IX_Customers_Name]</c>.
    /// </summary>
    internal static bool HeaderMentionsObject(string headerText, string objectName)
    {
        var position = headerText.IndexOf(objectName, StringComparison.OrdinalIgnoreCase);

        while (position >= 0)
        {
            var end = position + objectName.Length;

            // Either the header ends with the name, or the name is a prefix of a child
            // object ("[dbo].[Customers].[IX_Customers_Name]") or of the "..." suffix.
            if (end >= headerText.Length || headerText[end] == '.')
                return true;

            position = headerText.IndexOf(objectName, position + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
