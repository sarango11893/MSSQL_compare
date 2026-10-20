using SqlSuite.Core.Enums;
using SqlSuite.Core.Models;

namespace SqlSuite.Web.ViewModels;

/// <summary>
/// One row of the diff results matrix. Schema and data comparisons are projected onto the
/// same shape so the table renders identically in both modes.
/// </summary>
public sealed class DiffMatrixRow
{
    public required string StatusLabel { get; init; }

    /// <summary>CSS modifier of the status badge: <c>create</c>, <c>alter</c>, <c>drop</c> or <c>info</c>.</summary>
    public required string StatusClass { get; init; }

    public required string Name { get; init; }

    public required string Type { get; init; }

    public required string SourceState { get; init; }

    public required string TargetState { get; init; }

    public required string ActionLabel { get; init; }

    /// <summary>Set in schema mode; the row maps to a selectable object.</summary>
    public SchemaDifferenceInfo? Node { get; init; }

    /// <summary>Set in data mode.</summary>
    public TableDataDifference? Table { get; init; }

    public bool IsSelectable => Node is not null;

    public static DiffMatrixRow FromObject(SchemaDifferenceInfo node)
    {
        (string label, string css, string source, string target, string action) = node.UpdateAction switch
        {
            SchemaUpdateAction.Create => ("Solo origen", "create", "Existe", "Ausente", "Crear"),
            SchemaUpdateAction.Alter => ("Modificado", "alter", "Existe", "Distinto", "Actualizar"),
            _ => ("Solo destino", "drop", "Ausente", "Existe", "Eliminar"),
        };

        return new DiffMatrixRow
        {
            StatusLabel = label,
            StatusClass = css,
            Name = node.ObjectName,
            Type = node.DisplayName.Length > 0 ? node.DisplayName : node.ObjectType,
            SourceState = source,
            TargetState = target,
            ActionLabel = action,
            Node = node,
        };
    }

    public static DiffMatrixRow FromTable(TableDataDifference table)
    {
        var status = table.IsSkipped
            ? ("Omitida", "drop")
            : table.TotalCount == 0
                ? ("Sin cambios", "info")
                : ("Con cambios", "alter");

        return new DiffMatrixRow
        {
            StatusLabel = status.Item1,
            StatusClass = status.Item2,
            Name = table.Table.QualifiedName,
            Type = table.Table.KeyColumns.Count > 0
                ? $"Clave: {string.Join(", ", table.Table.KeyColumns)}"
                : "Sin clave",
            SourceState = $"{table.InsertCount} ins / {table.UpdateCount} upd",
            TargetState = $"{table.DeleteCount} del",
            ActionLabel = table.Summary,
            Table = table,
        };
    }
}
