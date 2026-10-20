using SchemaDifference = Microsoft.SqlServer.Dac.Compare.SchemaDifference;
using SqlSuite.Core.Enums;

namespace SqlSuite.Core.Models;

/// <summary>
/// Flattened, display friendly representation of a single node in the DacFx difference tree.
/// </summary>
public sealed class SchemaDifferenceInfo
{
    /// <summary>Fully qualified name of the object, for example <c>[dbo].[Products].[Sku]</c>.</summary>
    public string ObjectName { get; init; } = string.Empty;

    /// <summary>DacFx object class, for example <c>Table</c>, <c>Column</c> or <c>Index</c>.</summary>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>Localized description of the change ("Tabla", "Columna", ...) as reported by DacFx.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public SchemaUpdateAction UpdateAction { get; init; }

    /// <summary><see langword="true"/> when the node is an object, <see langword="false"/> when it is a property leaf.</summary>
    public bool IsObject { get; init; }

    /// <summary><see langword="true"/> when the change will be part of the generated script.</summary>
    public bool Included { get; init; }

    /// <summary>
    /// <see langword="true"/> when DacFx allows this node to be left out of the deployment script.
    /// Only top level objects are selectable; their children travel with them.
    /// </summary>
    public bool IsExcludable { get; init; }

    /// <summary>0 for top level objects, 1 for their children and so on.</summary>
    public int Depth { get; init; }

    public SchemaDifferenceInfo? Parent { get; init; }

    /// <summary>Top level object this node belongs to; a top level object returns itself.</summary>
    public SchemaDifferenceInfo Root => Parent is null ? this : Parent.Root;

    public IList<SchemaDifferenceInfo> Children { get; } = new List<SchemaDifferenceInfo>();

    /// <summary>Indented name used by the UI tree view.</summary>
    public string IndentedName => Depth == 0 ? ObjectName : $"{new string(' ', Depth * 4)}{ObjectName}";

    /// <summary>Live DacFx node, used to include or exclude this object from the script.</summary>
    internal SchemaDifference? Raw { get; init; }

    public override string ToString() => $"{UpdateAction,-6} {ObjectType,-22} {ObjectName}";
}
