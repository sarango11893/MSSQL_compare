using SqlSuite.Core.Enums;
using SqlSuite.Core.Models;

namespace SqlSuite.Web.ViewModels;

/// <summary>
/// Top level schema differences grouped by DacFx object type, ready to be rendered as a
/// collapsible section of the diff tree.
/// </summary>
public sealed class SchemaDiffGroup
{
    private static readonly Dictionary<string, (string Label, int Order)> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Table"] = ("Tablas", 0),
        ["View"] = ("Vistas", 1),
        ["Procedure"] = ("Procedimientos", 2),
        ["ScalarFunction"] = ("Funciones escalares", 3),
        ["TableValuedFunction"] = ("Funciones de tabla", 4),
        ["Function"] = ("Funciones", 5),
        ["Synonym"] = ("Sinónimos", 6),
        ["Sequence"] = ("Secuencias", 7),
        ["TableType"] = ("Tipos de tabla", 8),
        ["DataType"] = ("Tipos de datos", 9),
        ["Schema"] = ("Esquemas", 10),
        ["DmlTrigger"] = ("Disparadores (triggers)", 11),
    };

    public SchemaDiffGroup(string objectType, IReadOnlyList<SchemaDifferenceInfo> nodes)
    {
        ObjectType = objectType;
        Nodes = nodes;

        (string label, int order) = KnownTypes.TryGetValue(objectType, out var known)
            ? known
            : (objectType, 100);

        Label = label;
        Order = order;
    }

    public string ObjectType { get; }

    public string Label { get; }

    /// <summary>Display order; unmapped types fall to the end.</summary>
    public int Order { get; }

    public IReadOnlyList<SchemaDifferenceInfo> Nodes { get; }

    public int Count => Nodes.Count;

    public int CreateCount => Nodes.Count(n => n.UpdateAction == SchemaUpdateAction.Create);

    public int AlterCount => Nodes.Count(n => n.UpdateAction == SchemaUpdateAction.Alter);

    public int DropCount => Nodes.Count(n => n.UpdateAction == SchemaUpdateAction.Drop);

    /// <summary>Groups the given differences, preserving the original order inside each group.</summary>
    public static List<SchemaDiffGroup> Build(IEnumerable<SchemaDifferenceInfo> differences)
        => differences
            .Where(d => d.Depth == 0)
            .GroupBy(d => d.ObjectType, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SchemaDiffGroup(g.Key, g.ToList()))
            .OrderBy(g => g.Order)
            .ThenBy(g => g.Label, StringComparer.CurrentCulture)
            .ToList();
}
