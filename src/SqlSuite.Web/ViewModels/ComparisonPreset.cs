using SqlSuite.Core.Enums;

namespace SqlSuite.Web.ViewModels;

/// <summary>Main content tabs of the workspace.</summary>
public enum WorkspaceTab
{
    Visualizer,
    Script,
    TextDiff,
}

/// <summary>
/// A named bundle of comparison settings. Presets are real shortcuts over
/// <see cref="SqlSuite.Core.Models.ComparisonOptions"/>: they never fake results.
/// </summary>
public sealed record ComparisonPreset(string Name, string Description, string Tag, ComparisonMode Mode)
{
    public static IReadOnlyList<ComparisonPreset> All { get; } = new[]
    {
        new ComparisonPreset(
            "Diff de esquema",
            "Todos los objetos DDL, sin eliminar nada en el destino.",
            "DDL",
            ComparisonMode.Schema),
        new ComparisonPreset(
            "Discrepancia de datos",
            "Compara datos limitando la lectura a 100 filas por tabla.",
            "DML",
            ComparisonMode.Data),
        new ComparisonPreset(
            "Sincronización completa",
            "DDL + DML con eliminación de objetos ausentes en el origen.",
            "DDL+DML",
            ComparisonMode.Schema),
    };
}
