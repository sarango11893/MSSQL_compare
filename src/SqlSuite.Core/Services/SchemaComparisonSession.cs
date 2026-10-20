using Microsoft.SqlServer.Dac.Compare;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Services;

/// <summary>
/// Keeps the live DacFx comparison result alive after the comparison finishes so the caller can
/// include or exclude individual objects and regenerate the deployment script without reading
/// both databases again.
/// </summary>
/// <remarks>
/// The deployment script is generated once and the selection is applied by slicing the blocks of the
/// unchecked objects out of it (<see cref="ScriptObjectExtractor.Remove"/>). DacFx's own
/// <c>SchemaComparisonResult.Exclude</c> is not used: it returns <see langword="false"/> and leaves
/// the object included whenever the object has dependencies, and a failed exclude cannot be undone
/// on the same result instance because the cached generation state is never refreshed.
/// </remarks>
public sealed class SchemaComparisonSession
{
    private readonly SchemaComparisonResult _raw;
    private readonly List<SchemaDifferenceInfo> _roots;
    private readonly List<string> _baseErrors;
    private readonly List<string> _baseWarnings;
    private readonly string _databaseName;
    private readonly TimeSpan _compareDuration;
    private readonly int _propertyLeafCount;
    private readonly bool _generateScript;

    private string? _fullScript;
    private bool _scriptLoaded;

    internal SchemaComparisonSession(
        SchemaComparisonResult raw,
        List<SchemaDifferenceInfo> roots,
        List<string> errors,
        List<string> warnings,
        string databaseName,
        TimeSpan compareDuration,
        int propertyLeafCount,
        bool generateScript)
    {
        _raw = raw;
        _roots = roots;
        _baseErrors = errors;
        _baseWarnings = warnings;
        _databaseName = databaseName;
        _compareDuration = compareDuration;
        _propertyLeafCount = propertyLeafCount;
        _generateScript = generateScript;

        Result = BuildResult(Array.Empty<SchemaDifferenceInfo>());
    }

    /// <summary>Result of the latest <see cref="RegenerateScript"/> call; all objects included when the comparison finishes.</summary>
    public SchemaComparisonResultInfo Result { get; private set; }

    /// <summary>Top level objects, in DacFx order.</summary>
    public IReadOnlyList<SchemaDifferenceInfo> Differences => _roots;

    /// <summary>
    /// Applies a selection and returns a fresh result whose <see cref="SchemaComparisonResultInfo.Script"/>
    /// contains only the selected top level objects. Children always travel with their parent.
    /// </summary>
    /// <param name="excludedObjects">Top level objects the user unchecked.</param>
    public SchemaComparisonResultInfo RegenerateScript(IEnumerable<SchemaDifferenceInfo> excludedObjects)
    {
        ArgumentNullException.ThrowIfNull(excludedObjects);

        Result = BuildResult(new HashSet<SchemaDifferenceInfo>(excludedObjects));
        return Result;
    }

    private SchemaComparisonResultInfo BuildResult(IReadOnlyCollection<SchemaDifferenceInfo> excluded)
    {
        HashSet<SchemaDifferenceInfo> skipped = new(excluded);
        List<SchemaDifferenceInfo> selected = _roots.Where(root => !skipped.Contains(root)).ToList();

        List<string> errors = new(_baseErrors);
        List<string> warnings = new(_baseWarnings);
        string script = string.Empty;

        if (_generateScript && _raw.IsValid && !_raw.IsEqual && selected.Count > 0)
        {
            string? fullScript = GetFullScript(errors, warnings);

            if (fullScript is not null)
            {
                script = skipped.Count == 0
                    ? fullScript
                    : ScriptObjectExtractor.KeepOnly(fullScript, CollectObjectNames(selected));
            }
        }

        return new SchemaComparisonResultInfo
        {
            IsValid = _raw.IsValid,
            // A partial selection is reported as "not equal" because the deployable subset no longer
            // represents the full database.
            IsEqual = _raw.IsEqual && skipped.Count == 0,
            Script = script,
            Differences = _roots,
            Errors = errors,
            Warnings = warnings,
            TotalDifferenceCount = _propertyLeafCount + CountNodes(_roots),
            SelectedObjectCount = selected.Count,
            TotalObjectCount = _roots.Count,
            ExcludedObjectCount = skipped.Count,
            Duration = _compareDuration,
            Summary = BuildSummary(selected),
        };
    }

    private string? GetFullScript(List<string> errors, List<string> warnings)
    {
        if (_scriptLoaded)
            return _fullScript;

        _scriptLoaded = true;

        SchemaCompareScriptGenerationResult generation = _raw.GenerateScript(_databaseName);

        if (!generation.Success)
        {
            errors.Add(generation.Exception is null
                ? $"No se pudo generar el script: {generation.Message}"
                : $"No se pudo generar el script: {generation.Message} ({generation.Exception.Message})");
            return null;
        }

        // DacFx always returns a sqlcmd-mode script. Resolving the variables here means the script
        // the user copies is plain T-SQL that runs from any client, not only sqlcmd.
        NormalizedScript normalized = SqlCmdScriptProcessor.Normalize(generation.Script, _databaseName);
        _fullScript = normalized.Script;
        warnings.AddRange(normalized.Warnings);

        return _fullScript;
    }

    /// <summary>
    /// Names of the excluded objects plus the names of every child node. A table owns blocks whose
    /// header names the child object instead of the table itself ("Creando Restricción CHECK
    /// [dbo].[CK_Products_Price]"), so children have to be excluded explicitly.
    /// </summary>
    private static List<string> CollectObjectNames(IEnumerable<SchemaDifferenceInfo> roots)
    {
        List<string> names = new();
        Collect(roots);
        return names;

        void Collect(IEnumerable<SchemaDifferenceInfo> nodes)
        {
            foreach (SchemaDifferenceInfo node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.ObjectName))
                    names.Add(node.ObjectName);

                Collect(node.Children);
            }
        }
    }

    private string BuildSummary(IReadOnlyList<SchemaDifferenceInfo> selected)
    {
        if (!_raw.IsValid)
            return "La comparación no es válida. Revise los errores notificados.";

        if (_roots.Count == 0)
            return "Los esquemas son idénticos.";

        if (selected.Count == 0)
            return $"Ningún objeto seleccionado ({_roots.Count} disponible(s)).";

        string summary = SchemaComparerService.BuildSummary(isValid: true, isEqual: false, selected);

        return selected.Count == _roots.Count
            ? summary
            : $"{summary} Script parcial: {selected.Count} de {_roots.Count} objeto(s).";
    }

    private static int CountNodes(IEnumerable<SchemaDifferenceInfo> nodes)
        => nodes.Sum(node => 1 + CountNodes(node.Children));
}
