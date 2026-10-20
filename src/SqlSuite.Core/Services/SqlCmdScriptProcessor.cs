using System.Text;
using System.Text.RegularExpressions;

namespace SqlSuite.Core.Services;

/// <summary>Result of turning a DacFx deployment script into plain T-SQL.</summary>
/// <param name="Script">The script with all <c>sqlcmd</c> directives resolved.</param>
/// <param name="Warnings">Variables or directives that could not be resolved.</param>
public sealed record NormalizedScript(string Script, IReadOnlyList<string> Warnings);

/// <summary>
/// DacFx always emits deployment scripts in <c>sqlcmd</c> mode: <c>:setvar</c> declarations,
/// <c>:on error exit</c> directives, <c>$(Variable)</c> placeholders and a <c>SET NOEXEC ON</c>
/// guard. Such a script cannot be executed through <see cref="Microsoft.Data.SqlClient.SqlCommand"/>,
/// and worse, the guard makes it silently do nothing when the variables are not substituted.
/// This helper resolves the placeholders and strips the directives so the output is plain T-SQL.
/// </summary>
public static class SqlCmdScriptProcessor
{
    private static readonly Regex SetVarRegex = new(
        @"^[ \t]*:setvar[ \t]+(?<name>\w+)[ \t]+""(?<value>[^""]*)""[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex DirectiveRegex = new(
        @"^[ \t]*:(?:on[ \t]+error|r|include)\b.*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex VariableRegex = new(@"\$\((?<name>\w+)\)", RegexOptions.Compiled);

    // DacFx wraps everything in an "am I running under sqlcmd?" guard that calls SET NOEXEC ON,
    // which would silently turn the whole deployment into a no-op.
    private static readonly Regex SqlCmdGuardRegex = new(
        @"IF\s+N'[^']*'\s+NOT\s+LIKE\s+N'[^']*'\s+BEGIN.*?\bEND\b",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // The guard comes with an explanatory comment block that documents a behaviour the
    // normalized script no longer has, so the comment is dropped together with the guard.
    private static readonly Regex SqlCmdCommentRegex = new(
        @"/\*[^*]*(?:\*(?!/)[^*]*)*\*/",
        RegexOptions.Compiled);

    private static readonly Regex BatchSeparatorRegex = new(
        @"^[ \t]*GO(?:[ \t]+(?<count>\d+))?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>Resolves <c>sqlcmd</c> variables and removes the <c>sqlcmd</c> directives.</summary>
    /// <param name="script">The script produced by DacFx.</param>
    /// <param name="databaseName">Overrides the <c>DatabaseName</c> variable when provided.</param>
    public static NormalizedScript Normalize(string script, string? databaseName = null)
    {
        if (string.IsNullOrEmpty(script))
            return new NormalizedScript(string.Empty, Array.Empty<string>());

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (Match match in SetVarRegex.Matches(script))
            variables[match.Groups["name"].Value] = match.Groups["value"].Value;

        if (!string.IsNullOrWhiteSpace(databaseName))
            variables["DatabaseName"] = databaseName;

        var text = SqlCmdGuardRegex.Replace(script, string.Empty);
        text = SqlCmdCommentRegex.Replace(text, match =>
            match.Value.Contains("sqlcmd", StringComparison.OrdinalIgnoreCase) ? string.Empty : match.Value);
        text = SetVarRegex.Replace(text, string.Empty);
        text = DirectiveRegex.Replace(text, string.Empty);

        text = VariableRegex.Replace(text, match =>
        {
            var name = match.Groups["name"].Value;

            if (variables.TryGetValue(name, out var value))
                return value;

            warnings.Add($"La variable '{name}' no tiene valor; se reemplazó por una cadena vacía.");
            return string.Empty;
        });

        return new NormalizedScript(Tidy(text), warnings);
    }

    /// <summary>Splits a script on <c>GO</c> separators, honouring the <c>GO n</c> repeat syntax.</summary>
    public static IReadOnlyList<string> SplitBatches(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return Array.Empty<string>();

        var batches = new List<string>();
        var position = 0;

        foreach (Match separator in BatchSeparatorRegex.Matches(script))
        {
            // "GO n" repeats the batch that precedes it n times.
            AddBatch(batches, script[position..separator.Index], ParseRepeatCount(separator));
            position = separator.Index + separator.Length;
        }

        AddBatch(batches, script[position..], repeatCount: 1);
        return batches;
    }

    private static void AddBatch(List<string> batches, string batch, int repeatCount)
    {
        var trimmed = batch.Trim();

        if (trimmed.Length == 0)
            return;

        for (var i = 0; i < Math.Max(1, repeatCount); i++)
            batches.Add(trimmed);
    }

    private static int ParseRepeatCount(Match separator) =>
        separator.Groups["count"].Success && int.TryParse(separator.Groups["count"].Value, out var count)
            ? count
            : 1;

    private static string Tidy(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder(script.Length);
        var blankLines = 0;

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                blankLines++;
                if (blankLines > 1)
                    continue;
            }
            else
            {
                blankLines = 0;
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}
