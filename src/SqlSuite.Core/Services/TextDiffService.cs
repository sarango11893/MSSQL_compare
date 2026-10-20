using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

/// <summary>Creates a line-oriented diff without depending on deployment-script formatting.</summary>
public sealed class TextDiffService
{
    public IReadOnlyList<CodeDiffLine> Compare(string? oldText, string? newText)
    {
        string[] oldLines = SplitLines(oldText);
        string[] newLines = SplitLines(newText);
        List<(int Old, int New)> matches = new();
        FindMatches(oldLines, 0, oldLines.Length, newLines, 0, newLines.Length, matches);

        List<CodeDiffLine> result = new(oldLines.Length + newLines.Length);
        int oldIndex = 0;
        int newIndex = 0;

        foreach ((int oldMatch, int newMatch) in matches)
        {
            while (oldIndex < oldMatch)
            {
                result.Add(new CodeDiffLine(oldIndex + 1, oldLines[oldIndex], null, null, CodeDiffKind.Removed));
                oldIndex++;
            }

            while (newIndex < newMatch)
            {
                result.Add(new CodeDiffLine(null, null, newIndex + 1, newLines[newIndex], CodeDiffKind.Added));
                newIndex++;
            }

            result.Add(new CodeDiffLine(
                oldIndex + 1,
                oldLines[oldIndex],
                newIndex + 1,
                newLines[newIndex],
                CodeDiffKind.Unchanged));
            oldIndex++;
            newIndex++;
        }

        while (oldIndex < oldLines.Length)
        {
            result.Add(new CodeDiffLine(oldIndex + 1, oldLines[oldIndex], null, null, CodeDiffKind.Removed));
            oldIndex++;
        }

        while (newIndex < newLines.Length)
        {
            result.Add(new CodeDiffLine(null, null, newIndex + 1, newLines[newIndex], CodeDiffKind.Added));
            newIndex++;
        }

        return result;
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];

        return normalized.Split('\n');
    }

    private static void FindMatches(
        string[] oldLines,
        int oldStart,
        int oldLength,
        string[] newLines,
        int newStart,
        int newLength,
        List<(int Old, int New)> matches)
    {
        if (oldLength == 0 || newLength == 0)
            return;

        if (oldLength == 1)
        {
            for (int index = newStart; index < newStart + newLength; index++)
            {
                if (!string.Equals(oldLines[oldStart], newLines[index], StringComparison.Ordinal))
                    continue;

                matches.Add((oldStart, index));
                return;
            }

            return;
        }

        int leftLength = oldLength / 2;
        int[] leftScores = GetLcsLengths(oldLines, oldStart, leftLength, newLines, newStart, newLength, reverse: false);
        int[] rightScores = GetLcsLengths(
            oldLines, oldStart + leftLength, oldLength - leftLength,
            newLines, newStart, newLength, reverse: true);

        int split = 0;
        int bestScore = -1;
        for (int index = 0; index <= newLength; index++)
        {
            int score = leftScores[index] + rightScores[newLength - index];
            if (score <= bestScore)
                continue;

            split = index;
            bestScore = score;
        }

        FindMatches(oldLines, oldStart, leftLength, newLines, newStart, split, matches);
        FindMatches(
            oldLines, oldStart + leftLength, oldLength - leftLength,
            newLines, newStart + split, newLength - split, matches);
    }

    private static int[] GetLcsLengths(
        string[] oldLines,
        int oldStart,
        int oldLength,
        string[] newLines,
        int newStart,
        int newLength,
        bool reverse)
    {
        int[] previous = new int[newLength + 1];
        int[] current = new int[newLength + 1];

        for (int oldOffset = 0; oldOffset < oldLength; oldOffset++)
        {
            for (int newOffset = 1; newOffset <= newLength; newOffset++)
            {
                string oldLine = oldLines[reverse ? oldStart + oldLength - oldOffset - 1 : oldStart + oldOffset];
                string newLine = newLines[reverse ? newStart + newLength - newOffset : newStart + newOffset - 1];
                current[newOffset] = string.Equals(oldLine, newLine, StringComparison.Ordinal)
                    ? previous[newOffset - 1] + 1
                    : Math.Max(previous[newOffset], current[newOffset - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous;
    }
}
