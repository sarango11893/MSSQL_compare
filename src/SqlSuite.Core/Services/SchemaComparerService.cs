using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using SqlSuite.Core.Models;
using CoreSchemaUpdateAction = SqlSuite.Core.Enums.SchemaUpdateAction;
using DacSchemaUpdateAction = Microsoft.SqlServer.Dac.Compare.SchemaUpdateAction;

namespace SqlSuite.Core.Services;

/// <summary>
/// Module A: schema comparison (DDL) backed by the official DacFx comparison engine.
/// </summary>
/// <remarks>
/// The original template called <c>DacProfile.GetProfile</c> and a two connection string
/// <c>SchemaComparison</c> constructor; neither exists. DacFx expects two
/// <see cref="SchemaCompareDatabaseEndpoint"/> instances instead.
/// </remarks>
public sealed class SchemaComparerService
{
    /// <summary>
    /// Compares the schema of two databases and keeps the live DacFx result alive so the caller can
    /// later include or exclude individual objects and regenerate the script.
    /// </summary>
    public SchemaComparisonSession Compare(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ComparisonOptions();

        if (string.IsNullOrWhiteSpace(source.ServerName) || string.IsNullOrWhiteSpace(source.DatabaseName))
            throw new ArgumentException("La conexión de origen debe indicar un servidor y una base de datos.", nameof(source));

        if (string.IsNullOrWhiteSpace(target.ServerName) || string.IsNullOrWhiteSpace(target.DatabaseName))
            throw new ArgumentException("La conexión de destino debe indicar un servidor y una base de datos.", nameof(target));

        DateTime startedAt = DateTime.UtcNow;

        SchemaComparison comparison = new(
            new SchemaCompareDatabaseEndpoint(source.GetConnectionString()),
            new SchemaCompareDatabaseEndpoint(target.GetConnectionString()));

        ApplyOptions(comparison.Options, options);

        SchemaComparisonResult raw = comparison.Compare(cancellationToken);

        List<string> errors = new();
        List<string> warnings = new();

        foreach (DacSchemaComparisonMessage message in raw.GetErrors() ?? Enumerable.Empty<DacSchemaComparisonMessage>())
        {
            string text = string.IsNullOrWhiteSpace(message.ElementType)
                ? message.Message
                : $"{message.ElementType}: {message.Message}";

            if (message.MessageType == DacMessageType.Error)
                errors.Add(text);
            else
                warnings.Add(text);
        }

        bool isValid;
        bool isEqual = false;
        try
        {
            isValid = raw.IsValid;
            if (isValid)
                isEqual = raw.IsEqual;
        }
        catch (NullReferenceException ex)
        {
            isValid = false;
            errors.Add($"DacFx no pudo producir un resultado válido de comparación de esquema: {ex.Message}");
        }

        List<SchemaDifferenceInfo> roots = new();
        int propertyLeafCount = 0;

        if (isValid && raw.Differences is not null)
        {
            foreach (SchemaDifference difference in raw.Differences)
            {
                SchemaDifferenceInfo? mapped = Map(difference, parent: null, depth: 0, ref propertyLeafCount);
                if (mapped is not null)
                    roots.Add(mapped);
            }
        }

        string databaseName = string.IsNullOrWhiteSpace(target.DatabaseName) ? "SqlSuiteTarget" : target.DatabaseName;

        return new SchemaComparisonSession(
            raw,
            isValid,
            isEqual,
            roots,
            errors,
            warnings,
            databaseName,
            DateTime.UtcNow - startedAt,
            propertyLeafCount,
            options.GenerateDeploymentScript);
    }

    /// <summary>Asynchronous wrapper so the UI thread is never blocked while DacFx reads both databases.</summary>
    public Task<SchemaComparisonSession> CompareAsync(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        ComparisonOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Compare(source, target, options, cancellationToken), cancellationToken);

    private static void ApplyOptions(DacDeployOptions deploy, ComparisonOptions options)
    {
        deploy.DropObjectsNotInSource = options.DropObjectsNotInSource;
        deploy.BlockOnPossibleDataLoss = options.BlockOnPossibleDataLoss;
        deploy.AllowTableRecreation = options.AllowTableRecreation;
        deploy.IncludeTransactionalScripts = options.IncludeTransactionalScripts;
        deploy.ScriptDatabaseOptions = options.ScriptDatabaseOptions;
        deploy.IgnoreWhitespace = options.IgnoreWhitespace;
        deploy.IgnorePermissions = options.IgnorePermissions;
        deploy.IgnoreRoleMembership = options.IgnoreRoleMembership;
        deploy.IgnoreColumnCollation = options.IgnoreColumnCollation;
        deploy.IgnoreIndexOptions = options.IgnoreIndexOptions;
        deploy.GenerateSmartDefaults = options.GenerateSmartDefaults;
        deploy.IncludeCompositeObjects = options.IncludeCompositeObjects;
        deploy.VerifyDeployment = true;
        deploy.DropIndexesNotInSource = options.DropObjectsNotInSource;

        if (options.CommandTimeoutSeconds > 0)
            deploy.LongRunningCommandTimeout = options.CommandTimeoutSeconds;
    }

    private static SchemaDifferenceInfo? Map(SchemaDifference difference, SchemaDifferenceInfo? parent, int depth, ref int propertyLeafCount)
    {
        if (difference.DifferenceType == SchemaDifferenceType.Property)
        {
            // Property leaves carry no identity and their display names are not useful on their own,
            // so they are counted but not surfaced as tree nodes.
            propertyLeafCount++;
            return null;
        }

        TSqlObject? identity = difference.SourceObject ?? difference.TargetObject;

        string objectName = identity?.Name?.ToString()
            ?? difference.TargetObject?.Name?.ToString()
            ?? parent?.ObjectName
            ?? difference.Name;

        SchemaDifferenceInfo node = new()
        {
            ObjectName = objectName,
            ObjectType = identity?.ObjectType?.Name ?? difference.TargetObject?.ObjectType?.Name ?? "Object",
            DisplayName = difference.Name,
            UpdateAction = ToCore(difference.UpdateAction),
            IsObject = true,
            Included = difference.Included,
            IsExcludable = difference.IsExcludable,
            Raw = difference,
            Depth = depth,
            Parent = parent,
            SourceScript = depth == 0 ? GetScript(difference.SourceObject) : string.Empty,
            TargetScript = depth == 0 ? GetScript(difference.TargetObject) : string.Empty,
        };

        if (difference.Children is not null)
        {
            foreach (SchemaDifference child in difference.Children)
            {
                SchemaDifferenceInfo? mappedChild = Map(child, node, depth + 1, ref propertyLeafCount);
                if (mappedChild is not null)
                    node.Children.Add(mappedChild);
            }
        }

        return node;
    }

    private static string GetScript(TSqlObject? databaseObject)
        => databaseObject is not null && databaseObject.TryGetScript(out string script)
            ? script
            : string.Empty;

    private static CoreSchemaUpdateAction ToCore(DacSchemaUpdateAction action) => action switch
    {
        DacSchemaUpdateAction.Add => CoreSchemaUpdateAction.Create,
        DacSchemaUpdateAction.Delete => CoreSchemaUpdateAction.Drop,
        _ => CoreSchemaUpdateAction.Alter,
    };

    internal static string BuildSummary(bool isValid, bool isEqual, IReadOnlyList<SchemaDifferenceInfo> roots)
    {
        if (!isValid)
            return "La comparación no es válida. Revise los errores notificados.";

        if (isEqual)
            return "Los esquemas son idénticos.";

        int created = roots.Count(r => r.UpdateAction == CoreSchemaUpdateAction.Create);
        int altered = roots.Count(r => r.UpdateAction == CoreSchemaUpdateAction.Alter);
        int dropped = roots.Count(r => r.UpdateAction == CoreSchemaUpdateAction.Drop);

        List<string> parts = new(3);
        if (created > 0) parts.Add($"{created} objeto(s) a crear");
        if (altered > 0) parts.Add($"{altered} objeto(s) a modificar");
        if (dropped > 0) parts.Add($"{dropped} objeto(s) a eliminar");

        return parts.Count == 0 ? "Sin diferencias." : string.Join(", ", parts) + ".";
    }
}
