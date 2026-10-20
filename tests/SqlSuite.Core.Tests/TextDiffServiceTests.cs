using SqlSuite.Core.Models;
using SqlSuite.Core.Services;

namespace SqlSuite.Core.Tests;

public sealed class TextDiffServiceTests
{
    private readonly TextDiffService _service = new();

    [Fact]
    public void Compare_UnchangedText_ReturnsOnlyUnchangedLines()
    {
        IReadOnlyList<CodeDiffLine> result = _service.Compare("SELECT 1;\r\nGO\r\n", "SELECT 1;\nGO\n");

        Assert.Collection(result,
            line => Assert.Equal((1, "SELECT 1;", 1, "SELECT 1;", CodeDiffKind.Unchanged),
                (line.OldLineNumber, line.OldText, line.NewLineNumber, line.NewText, line.Kind)),
            line => Assert.Equal((2, "GO", 2, "GO", CodeDiffKind.Unchanged),
                (line.OldLineNumber, line.OldText, line.NewLineNumber, line.NewText, line.Kind)));
    }

    [Fact]
    public void Compare_ChangedLine_MarksOldAndNewText()
    {
        IReadOnlyList<CodeDiffLine> result = _service.Compare("CREATE PROC p AS\nSELECT 1;", "CREATE PROC p AS\nSELECT 2;");

        Assert.Equal(3, result.Count);
        Assert.Equal(CodeDiffKind.Unchanged, result[0].Kind);
        Assert.Equal((2, "SELECT 1;"), (result[1].OldLineNumber, result[1].OldText));
        Assert.Equal(CodeDiffKind.Removed, result[1].Kind);
        Assert.Equal((2, "SELECT 2;"), (result[2].NewLineNumber, result[2].NewText));
        Assert.Equal(CodeDiffKind.Added, result[2].Kind);
    }

    [Fact]
    public void Compare_AddedOrRemovedObjectDefinition_ShowsAvailableSide()
    {
        IReadOnlyList<CodeDiffLine> added = _service.Compare(null, "CREATE VIEW v AS\nSELECT 1;");
        IReadOnlyList<CodeDiffLine> removed = _service.Compare("CREATE VIEW v AS\nSELECT 1;", null);

        Assert.All(added, line => Assert.Equal(CodeDiffKind.Added, line.Kind));
        Assert.All(removed, line => Assert.Equal(CodeDiffKind.Removed, line.Kind));
    }

    [Fact]
    public void Compare_LargeInputs_PreservesCommonLinesAndFindsChanges()
    {
        string oldText = string.Join('\n', Enumerable.Range(0, 1_000).Select(index => $"SELECT {index};"));
        string newText = oldText.Replace("SELECT 500;", "SELECT 501;", StringComparison.Ordinal);

        IReadOnlyList<CodeDiffLine> result = _service.Compare(oldText, newText);

        Assert.Equal(1_001, result.Count);
        Assert.Single(result, line => line.Kind == CodeDiffKind.Removed);
        Assert.Single(result, line => line.Kind == CodeDiffKind.Added);
    }
}
