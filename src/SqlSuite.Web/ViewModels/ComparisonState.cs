using SqlSuite.Core.Enums;
using SqlSuite.Core.Models;
using SqlSuite.Core.Services;

namespace SqlSuite.Web.ViewModels;

/// <summary>
/// Shell state: owns both connection panels, the schema/data comparison results and the
/// operations that drive them. Blazor re-renders on <see cref="Changed"/>, so no
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/> is required.
/// </summary>
public sealed class ComparisonState
{
    private readonly SchemaComparerService _schemaComparer = new();
    private readonly DataComparerService _dataComparer = new();
    private readonly ScriptExecutionService _executionService = new();
    private readonly ComparisonOptions _options = new();

    private ComparisonMode _mode = ComparisonMode.Schema;
    private string _statusMessage = "Listo.";
    private string _script = string.Empty;
    private string _summary = string.Empty;
    private readonly HashSet<SchemaDifferenceInfo> _excludedObjects = new();
    private readonly HashSet<RowChange> _selectedRows = new();
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scriptGate = new(1, 1);

    private SchemaComparisonSession? _session;
    private bool _sessionDropObjectsNotInSource;
    private int _scriptVersion;
    private string _searchTerm = string.Empty;
    private SchemaScriptView _scriptView = SchemaScriptView.Final;
    private SchemaDifferenceInfo? _focusedObject;
    private WorkspaceTab _activeTab = WorkspaceTab.Visualizer;
    private string? _activePresetName;
    private bool _useTreeView;

    private bool _isBusy;
    private bool _isApplyConfirmationVisible;
    private bool _isRegeneratingScript;
    private bool _isConnectionDrawerVisible;
    private CancellationTokenSource? _cts;

    public ComparisonState()
    {
        Source = new ConnectionState("Origen (source)");
        Target = new ConnectionState("Destino (target)");
    }

    /// <summary>Raised whenever a value the UI renders may have changed.</summary>
    public event Action? Changed;

    public ConnectionState Source { get; }

    public ConnectionState Target { get; }

    public List<SchemaDifferenceInfo> SchemaDifferences { get; } = new();

    public List<TableDataDifference> TableDifferences { get; } = new();

    /// <summary>Chronological console output rendered by the "Execution log" panel.</summary>
    public List<LogEntry> Log { get; } = new();

    public IReadOnlyList<ComparisonMode> Modes { get; } = new[] { ComparisonMode.Schema, ComparisonMode.Data };


    /// <summary>Schema comparison keeps DacFx options, data comparison keeps row options.</summary>
    public ComparisonMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            _mode = value;
            ClearResults();
            StatusMessage = value == ComparisonMode.Schema
                ? "Modo esquema (DDL) seleccionado."
                : "Modo datos (DML) seleccionado.";
            AddLog("Mode", value == ComparisonMode.Schema
                ? "Modo cambiado a comparación de esquema (DDL)."
                : "Modo cambiado a comparación de datos (DML).");
            Notify();
        }
    }

    public bool IsSchemaMode => Mode == ComparisonMode.Schema;

    public bool IsDataMode => Mode == ComparisonMode.Data;

    // -------------------------------------------------------------- workspace tabs

    /// <summary>Tab selected in the main area: the difference matrix or the generated script.</summary>
    public WorkspaceTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab == value)
                return;

            _activeTab = value;
            Notify();
        }
    }

    public void ShowTab(WorkspaceTab tab) => ActiveTab = tab;

    /// <summary><see langword="true"/> while the connection settings drawer is open.</summary>
    public bool IsConnectionDrawerVisible
    {
        get => _isConnectionDrawerVisible;
        private set
        {
            _isConnectionDrawerVisible = value;
            Notify();
        }
    }

    public void ShowConnectionDrawer() => IsConnectionDrawerVisible = true;

    public void HideConnectionDrawer() => IsConnectionDrawerVisible = false;

    /// <summary>Schema results can be shown as a flat matrix or as the grouped tree.</summary>
    public bool UseTreeView
    {
        get => _useTreeView;
        set
        {
            if (_useTreeView == value)
                return;

            _useTreeView = value;
            Notify();
        }
    }

    // ------------------------------------------------------------------- presets

    public IReadOnlyList<ComparisonPreset> Presets => ComparisonPreset.All;

    public string? ActivePresetName
    {
        get => _activePresetName;
        private set
        {
            if (_activePresetName == value)
                return;

            _activePresetName = value;
            Notify();
        }
    }

    /// <summary>Applies the preset's mode and options. It does not run the comparison.</summary>
    public void ApplyPreset(ComparisonPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        Mode = preset.Mode;

        switch (preset.Name)
        {
            case "Diff de esquema":
                DropObjectsNotInSource = false;
                MaxRowsPerTable = 100_000;
                break;

            case "Discrepancia de datos":
                DropObjectsNotInSource = false;
                MaxRowsPerTable = 100;
                break;

            case "Sincronización completa":
                DropObjectsNotInSource = true;
                MaxRowsPerTable = 100_000;
                break;
        }

        ActivePresetName = preset.Name;
        AddLog("Preset", $"Aplicado: {preset.Name} ({preset.Tag}).");
        StatusMessage = $"Preset aplicado: {preset.Name}.";
    }

    // ---------------------------------------------------------------------- KPIs

    /// <summary>Number of top level objects examined by the last schema comparison.</summary>
    public int ObjectsInspected => IsSchemaMode ? TotalObjectCount : TableDifferences.Count;

    /// <summary>Objects that only exist in the source (they become CREATE statements).</summary>
    public int SourceOnlyCount => IsSchemaMode
        ? SchemaDifferences.Count(d => d.Depth == 0 && d.UpdateAction == SchemaUpdateAction.Create)
        : TableDifferences.Sum(t => t.InsertCount);

    /// <summary>Objects that changed on both sides (ALTER / UPDATE).</summary>
    public int ModifiedCount => IsSchemaMode
        ? SchemaDifferences.Count(d => d.Depth == 0 && d.UpdateAction == SchemaUpdateAction.Alter)
        : TableDifferences.Sum(t => t.UpdateCount);

    /// <summary>Objects that only exist in the target (they become DROP / DELETE statements).</summary>
    public int TargetOnlyCount => IsSchemaMode
        ? SchemaDifferences.Count(d => d.Depth == 0 && d.UpdateAction == SchemaUpdateAction.Drop)
        : TableDifferences.Sum(t => t.DeleteCount);

    public string ObjectsInspectedLabel => IsSchemaMode ? "Objetos inspeccionados" : "Tablas inspeccionadas";

    /// <summary>Rows rendered by the diff results matrix.</summary>
    public IReadOnlyList<DiffMatrixRow> MatrixRows => IsSchemaMode
        ? FilteredSchemaGroups
            .SelectMany(g => g.Nodes.Select(DiffMatrixRow.FromObject))
            .ToList()
        : TableDifferences.Select(DiffMatrixRow.FromTable).ToList();

    public int MatrixRowCount => MatrixRows.Count;

    public bool HasMatrixRows => MatrixRowCount > 0;

    // ----------------------------------------------------------------- log entries

    public void AddLog(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Log.Add(new LogEntry(DateTime.Now, category, message.Trim()));
        Notify();
    }

    public void ClearLog()
    {
        Log.Clear();
        Notify();
    }

    /// <summary>Re-tests both endpoints so the container status cards show live state.</summary>
    public async Task RefreshHealthAsync()
    {
        AddLog("Health", "Comprobando la conectividad de los contenedores…");

        await ProbeHealthAsync(Source);
        await ProbeHealthAsync(Target);

        StatusMessage = "Estado de los contenedores actualizado.";
        Notify();
    }

    private static async Task ProbeHealthAsync(ConnectionState connection)
    {
        // TestConnectionAsync already resolves the health state (Online/Offline).
        await connection.TestConnectionAsync();
    }

    /// <summary>Destination of a "clear results" request from the toolbar.</summary>
    public void ResetResults()
    {
        ClearResults();
        AddLog("System", "Resultados y log limpiados.");
        StatusMessage = "Listo.";
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessage = value;
            Notify();
        }
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            _summary = value;
            Notify();
        }
    }

    public string Script
    {
        get => _script;
        private set
        {
            _script = value;
            Notify();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            Notify();
        }
    }

    public bool HasScript => !string.IsNullOrEmpty(Script);

    public bool CanCompare => !IsBusy;

    public bool CanApplyScript => !IsBusy && !IsRegeneratingScript && HasScript;

    /// <summary>Drives the in-app replacement for the WPF confirmation dialog.</summary>
    public bool IsApplyConfirmationVisible
    {
        get => _isApplyConfirmationVisible;
        private set
        {
            _isApplyConfirmationVisible = value;
            Notify();
        }
    }

    public string ApplyConfirmationText => Mode == ComparisonMode.Schema
        ? $"Se va a aplicar el script de esquema (DDL) sobre '{Target.ToConnectionInfo()}'. ¿Desea continuar?"
        : $"Se va a aplicar el script de datos (DML) sobre '{Target.ToConnectionInfo()}'. ¿Desea continuar?";

    // ------------------------------------------------- object selection (schema mode)

    /// <summary>Top level differences grouped by object type, in display order.</summary>
    public IReadOnlyList<SchemaDiffGroup> SchemaGroups => SchemaDiffGroup.Build(SchemaDifferences);

    /// <summary>Groups filtered by <see cref="SearchTerm"/>; the full list when there is no search.</summary>
    public IReadOnlyList<SchemaDiffGroup> FilteredSchemaGroups
    {
        get
        {
            var groups = SchemaGroups;
            var term = _searchTerm.Trim();

            if (term.Length == 0)
                return groups;

            return groups
                .Select(group => new SchemaDiffGroup(
                    group.ObjectType,
                    group.Nodes.Where(node => Matches(node, term)).ToList()))
                .Where(group => group.Nodes.Count > 0)
                .ToList();
        }
    }

    /// <summary>Free text that filters the tree by object name or type. Display only: never changes the selection.</summary>
    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            var normalized = value ?? string.Empty;
            if (_searchTerm == normalized)
                return;

            _searchTerm = normalized;
            Notify();
        }
    }

    public bool HasSearchTerm => _searchTerm.Trim().Length > 0;

    public int SearchMatchCount => HasSearchTerm
        ? IsSchemaMode
            ? FilteredSchemaGroups.Sum(g => g.Count)
            : FilteredDataGroups.Sum(g => g.Changes.Count)
        : IsSchemaMode ? TotalObjectCount : FilteredDataGroups.Sum(g => g.Changes.Count);

    public bool HasSearchResults => !HasSearchTerm || SearchMatchCount > 0;

    public void ClearSearch() => SearchTerm = string.Empty;

    /// <summary>Table groups filtered by table name, key, row key, changed columns, or row values.</summary>
    public IReadOnlyList<FilteredDataTableGroup> FilteredDataGroups
    {
        get
        {
            var term = _searchTerm.Trim();
            return TableDifferences
                .Select(table =>
                {
                    var tableMatches = term.Length == 0 || MatchesTable(table, term);
                    var changes = tableMatches
                        ? table.Changes
                        : table.Changes.Where(change => MatchesChange(change, term)).ToList();

                    return new FilteredDataTableGroup(table, changes);
                })
                .Where(group => group.Changes.Count > 0
                    || group.Table.IsSkipped && (term.Length == 0 || MatchesTable(group.Table, term))
                    || term.Length == 0 && group.Table.TotalCount == 0
                    || term.Length > 0 && MatchesTable(group.Table, term))
                .ToList();
        }
    }

    /// <summary><see langword="true"/> when the script panel shows a single object instead of the final script.</summary>
    public bool IsObjectScriptView => _scriptView == SchemaScriptView.Object && _focusedObject is not null;

    /// <summary>Object whose script is shown while <see cref="SchemaScriptView.Object"/> is active.</summary>
    public SchemaDifferenceInfo? FocusedObject => _focusedObject;

    /// <summary>Script rendered in the panel: either the focused object's blocks or the whole deployment.</summary>
    public string DisplayedScript => IsObjectScriptView
        ? ScriptObjectExtractor.Extract(Script, new[] { _focusedObject!.ObjectName })
        : Script;

    public bool HasDisplayedScript => !string.IsNullOrEmpty(DisplayedScript);

    /// <summary>Title of the script panel, so the user always knows what is being shown.</summary>
    public string ScriptTitle => IsObjectScriptView
        ? $"Script de {_focusedObject!.ObjectName}"
        : IsSchemaMode
            ? $"Script final ({SelectedObjectCount} de {TotalObjectCount} objeto(s))"
            : $"Script final ({SelectedRowCount} registro(s) seleccionado(s))";

    /// <summary>Shows the whole deployment script; available whenever a selection exists.</summary>
    public void ShowFinalScript()
    {
        if (_scriptView == SchemaScriptView.Final)
            return;

        _scriptView = SchemaScriptView.Final;
        Notify();
    }

    /// <summary>Shows only the blocks of <paramref name="node"/>'s top level object.</summary>
    public void ShowObjectScript(SchemaDifferenceInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var root = node.Root;
        _focusedObject = root;
        _scriptView = SchemaScriptView.Object;
        Notify();
    }

    public bool HasSchemaSelection => SchemaDifferences.Count > 0;

    public int SelectedObjectCount => SchemaDifferences.Count(IsSelected);

    public int TotalObjectCount => SchemaDifferences.Count;

    public int SelectedRowCount => _selectedRows.Count;

    public int TotalRowCount => TableDifferences.Sum(table => table.TotalCount);

    public int SelectedDataTableCount => TableDifferences.Count(table =>
        table.Changes.Any(_selectedRows.Contains));

    public bool IsRowSelected(RowChange change) => _selectedRows.Contains(change);

    public int SelectedRowsInTable(TableDataDifference table)
        => table.Changes.Count(_selectedRows.Contains);

    public bool IsTableFullySelected(TableDataDifference table)
        => table.TotalCount > 0 && SelectedRowsInTable(table) == table.TotalCount;

    public bool IsTablePartiallySelected(TableDataDifference table)
    {
        var selected = SelectedRowsInTable(table);
        return selected > 0 && selected < table.TotalCount;
    }

    /// <summary>Includes or excludes one DML record from the generated script.</summary>
    public void SetRowSelected(RowChange change, bool selected)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (!TableDifferences.Any(table => !table.IsSkipped && table.Changes.Contains(change)))
            return;

        if (selected)
            _selectedRows.Add(change);
        else
            _selectedRows.Remove(change);

        RegenerateDataScript();
    }

    /// <summary>Includes or excludes every DML record in a table.</summary>
    public void SetTableRowsSelected(TableDataDifference table, bool selected)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.IsSkipped)
            return;

        foreach (var change in table.Changes)
        {
            if (selected)
                _selectedRows.Add(change);
            else
                _selectedRows.Remove(change);
        }

        RegenerateDataScript();
    }

    /// <summary>Includes every discovered DML record. Nothing is selected automatically.</summary>
    public void SelectAllDataRows()
    {
        _selectedRows.Clear();
        foreach (var change in TableDifferences
                     .Where(table => !table.IsSkipped)
                     .SelectMany(table => table.Changes))
            _selectedRows.Add(change);

        RegenerateDataScript();
    }

    public void SelectNoDataRows()
    {
        _selectedRows.Clear();
        RegenerateDataScript();
    }

    public static string RowChangeKindLabel(RowChange change) => change.Kind switch
    {
        RowChangeKind.Insert => "INSERTAR",
        RowChangeKind.Update => "ACTUALIZAR",
        RowChangeKind.Delete => "ELIMINAR",
        _ => change.Kind.ToString().ToUpperInvariant(),
    };

    public static string RowChangeKindClass(RowChange change) => change.Kind switch
    {
        RowChangeKind.Insert => "create",
        RowChangeKind.Update => "alter",
        RowChangeKind.Delete => "drop",
        _ => "info",
    };

    public static string RowChangeDetails(RowChange change)
        => change.Kind switch
        {
            RowChangeKind.Update when change.ChangedColumns.Count > 0 =>
                string.Join(", ", change.ChangedColumns.Select(column =>
                    $"{column.ColumnName}: {FormatValue(column.TargetValue)} → {FormatValue(column.SourceValue)}")),
            RowChangeKind.Insert => FormatRowValues(change.SourceValues),
            RowChangeKind.Delete => FormatRowValues(change.TargetValues),
            _ => change.KeyDisplay,
        };

    private static string FormatRowValues(IReadOnlyDictionary<string, object?> values)
    {
        var preview = values.Take(4)
            .Select(pair => $"{pair.Key} = {FormatValue(pair.Value)}")
            .ToList();

        var remaining = values.Count - preview.Count;
        if (remaining > 0)
            preview.Add($"+{remaining} más");

        return string.Join(", ", preview);
    }

    private static string FormatValue(object? value)
        => value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty,
        };

    private static bool MatchesTable(TableDataDifference table, string term)
        => table.Table.QualifiedName.Contains(term, StringComparison.OrdinalIgnoreCase)
           || table.Table.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
           || string.Join(" ", table.Table.KeyColumns).Contains(term, StringComparison.OrdinalIgnoreCase)
           || table.SkipReason?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MatchesChange(RowChange change, string term)
        => change.KeyDisplay.Contains(term, StringComparison.OrdinalIgnoreCase)
           || change.ChangedColumns.Any(column =>
               column.ColumnName.Contains(term, StringComparison.OrdinalIgnoreCase)
               || FormatValue(column.SourceValue).Contains(term, StringComparison.OrdinalIgnoreCase)
               || FormatValue(column.TargetValue).Contains(term, StringComparison.OrdinalIgnoreCase))
           || change.SourceValues.Any(pair =>
               pair.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
               || FormatValue(pair.Value).Contains(term, StringComparison.OrdinalIgnoreCase))
           || change.TargetValues.Any(pair =>
               pair.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
               || FormatValue(pair.Value).Contains(term, StringComparison.OrdinalIgnoreCase));

    private void RegenerateDataScript()
    {
        var generator = new DataScriptGenerator(_options.Clone());
        var selectedTables = TableDifferences
            .Where(table => !table.IsSkipped && table.Changes.Any(_selectedRows.Contains))
            .Select(table => new TableDataDifference
            {
                Table = table.Table,
                Changes = table.Changes.Where(_selectedRows.Contains).ToList(),
                SkipReason = table.SkipReason,
                Warnings = table.Warnings,
            })
            .ToList();

        Script = generator.Generate(selectedTables);
        StatusMessage = SelectedRowCount == 0
            ? "No hay registros seleccionados para generar el script."
            : $"Script DML actualizado: {SelectedRowCount} registro(s) de {SelectedDataTableCount} tabla(s).";
        Notify();
    }

    private static bool Matches(SchemaDifferenceInfo node, string term)
        => node.ObjectName.Contains(term, StringComparison.OrdinalIgnoreCase)
           || node.ObjectType.Contains(term, StringComparison.OrdinalIgnoreCase)
           || node.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
           || node.Children.Any(child => Matches(child, term));

    /// <summary><see langword="true"/> while the deployment script is being regenerated for the current selection.</summary>
    public bool IsRegeneratingScript
    {
        get => _isRegeneratingScript;
        private set
        {
            _isRegeneratingScript = value;
            Notify();
        }
    }

    /// <summary>A top level object must be explicitly selected; children follow their parent.</summary>
    public bool IsSelected(SchemaDifferenceInfo node) => !_excludedObjects.Contains(node.Root);

    public bool IsGroupCollapsed(string objectType) => _collapsedGroups.Contains(objectType);

    public void ToggleGroup(string objectType)
    {
        if (!_collapsedGroups.Add(objectType))
            _collapsedGroups.Remove(objectType);

        Notify();
    }

    public bool IsGroupFullySelected(SchemaDiffGroup group) => group.Nodes.All(IsSelected);

    public bool IsGroupPartiallySelected(SchemaDiffGroup group)
        => !IsGroupFullySelected(group) && group.Nodes.Any(IsSelected);

    /// <summary>Checks or unchecks every object of one group and refreshes the script.</summary>
    public void SetGroupSelected(SchemaDiffGroup group, bool selected)
    {
        ArgumentNullException.ThrowIfNull(group);

        foreach (var node in group.Nodes)
            SetExcluded(node, !selected);

        ScheduleScriptRegeneration();
        Notify();
    }

    /// <summary>Checks or unchecks a single object and refreshes the script.</summary>
    public void SetObjectSelected(SchemaDifferenceInfo node, bool selected)
    {
        ArgumentNullException.ThrowIfNull(node);

        SetExcluded(node, !selected);
        ScheduleScriptRegeneration();
        Notify();
    }

    public void SelectAllObjects()
    {
        _excludedObjects.Clear();
        ScheduleScriptRegeneration();
        Notify();
    }

    public void SelectNoObjects()
    {
        foreach (var node in SchemaDifferences)
            _excludedObjects.Add(node.Root);

        ScheduleScriptRegeneration();
        Notify();
    }

    /// <summary>
    /// Rebuilds the deployment script with only the selected objects.
    /// DacFx needs ~4s per regeneration, so caller initiated changes are debounced.
    /// </summary>
    public Task RegenerateScriptAsync() => ScheduleScriptRegeneration(debounceMilliseconds: 0);

    private void SetExcluded(SchemaDifferenceInfo node, bool excluded)
    {
        // Children are never selectable on their own: they travel with their top level object.
        var root = node.Root;

        if (excluded)
            _excludedObjects.Add(root);
        else
            _excludedObjects.Remove(root);
    }

    private Task ScheduleScriptRegeneration(int debounceMilliseconds = 350)
    {
        if (_session is null || Mode != ComparisonMode.Schema || SchemaDifferences.Count == 0)
            return Task.CompletedTask;

        // A monotonically increasing version replaces the old CancellationTokenSource debounce:
        // disposing a shared CTS from the async continuation used to invalidate the field and
        // threw ObjectDisposedException on the next selection change.
        var version = Interlocked.Increment(ref _scriptVersion);

        return RegenerateScriptCoreAsync(debounceMilliseconds, version);
    }

    private async Task RegenerateScriptCoreAsync(int debounceMilliseconds, int version)
    {
        try
        {
            if (debounceMilliseconds > 0)
                await Task.Delay(debounceMilliseconds);

            if (version != Volatile.Read(ref _scriptVersion))
                return;

            await _scriptGate.WaitAsync();
            try
            {
                var session = _session;
                if (session is null || version != Volatile.Read(ref _scriptVersion))
                    return;

                IsRegeneratingScript = true;

                var excluded = _excludedObjects.ToList();
                var result = await Task.Run(() => session.RegenerateScript(excluded));

                if (version != Volatile.Read(ref _scriptVersion))
                    return;

                Script = result.Script;
                Summary = result.Summary;

                foreach (var error in result.Errors)
                {
                    if (!Log.Any(entry => entry.Category == "Error"
                        && string.Equals(entry.Message, error, StringComparison.Ordinal)))
                    {
                        AddLog("Error", error);
                    }
                }

                var selectedDrops = result.Differences.Count(difference =>
                    difference.UpdateAction == SchemaUpdateAction.Drop
                    && !_excludedObjects.Contains(difference.Root));
                var dropWarning = !_sessionDropObjectsNotInSource && selectedDrops > 0
                    ? _options.DropObjectsNotInSource
                        ? $"{selectedDrops} eliminación(es) seleccionada(s) no se generan porque la comparación se ejecutó con «Eliminar objetos ausentes en el origen» desactivado. Vuelve a comparar."
                        : $"{selectedDrops} eliminación(es) seleccionada(s) no se generan porque «Eliminar objetos ausentes en el origen» está desactivado. Actívalo y vuelve a comparar."
                    : null;

                if (dropWarning is not null
                    && !Log.Any(entry => entry.Category == "Aviso"
                        && string.Equals(entry.Message, dropWarning, StringComparison.Ordinal)))
                {
                    AddLog("Aviso", dropWarning);
                }

                var regenerationStatus = !string.IsNullOrWhiteSpace(result.Script)
                    ? result.ExcludedObjectCount == 0
                        ? "Script regenerado con todos los objetos."
                        : $"Script regenerado con {result.SelectedObjectCount} de {result.TotalObjectCount} objeto(s)."
                    : result.Errors.Count > 0
                        ? "No se pudo generar el script. Revisa los errores del log."
                        : "La selección actual no genera instrucciones SQL.";
                StatusMessage = dropWarning is null
                    ? regenerationStatus
                    : $"{regenerationStatus} {dropWarning}";
            }
            finally
            {
                _scriptGate.Release();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al regenerar el script: {ex.Message}";
            AddLog("Error", ex.Message);
        }
        finally
        {
            if (version == Volatile.Read(ref _scriptVersion))
                IsRegeneratingScript = false;
        }
    }

    // ------------------------------------------------------------- options

    public bool DropObjectsNotInSource
    {
        get => _options.DropObjectsNotInSource;
        set
        {
            if (_options.DropObjectsNotInSource == value)
                return;

            _options.DropObjectsNotInSource = value;
            Notify();
        }
    }

    public bool BlockOnPossibleDataLoss
    {
        get => _options.BlockOnPossibleDataLoss;
        set
        {
            if (_options.BlockOnPossibleDataLoss == value)
                return;

            _options.BlockOnPossibleDataLoss = value;
            Notify();
        }
    }

    public bool IncludeTransactionalScripts
    {
        get => _options.IncludeTransactionalScripts;
        set
        {
            if (_options.IncludeTransactionalScripts == value)
                return;

            _options.IncludeTransactionalScripts = value;
            _options.UseTransactionForDataScript = value;
            Notify();
        }
    }

    public bool CaseSensitiveDataComparison
    {
        get => _options.CaseSensitiveDataComparison;
        set
        {
            if (_options.CaseSensitiveDataComparison == value)
                return;

            _options.CaseSensitiveDataComparison = value;
            Notify();
        }
    }

    public int MaxRowsPerTable
    {
        get => _options.MaxRowsPerTable;
        set
        {
            var normalized = Math.Max(1, value);
            if (_options.MaxRowsPerTable == normalized)
                return;

            _options.MaxRowsPerTable = normalized;
            Notify();
        }
    }

    // ------------------------------------------------------------ commands

    public async Task CompareAsync()
    {
        if (IsBusy)
            return;

        ClearResults();

        var source = Source.ToConnectionInfo();
        var target = Target.ToConnectionInfo();

        if (!ValidateConnection(source, "origen") || !ValidateConnection(target, "destino"))
            return;

        IsBusy = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusMessage = Mode == ComparisonMode.Schema ? "Comparando esquemas…" : "Comparando datos…";
        AddLog("System", $"Comparación iniciada · origen {source} · destino {target}.");

        try
        {
            await RunComparisonAsync(source, target, cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Comparación cancelada.";
            AddLog("System", "Comparación cancelada por el usuario.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            AddLog("Error", ex.Message);
            Notify();
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _cts = null;

            cts.Dispose();
            IsBusy = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void RequestApplyScript()
    {
        if (!CanApplyScript)
            return;

        if (!ValidateConnection(Target.ToConnectionInfo(), "destino"))
            return;

        IsApplyConfirmationVisible = true;
    }

    public void DismissApplyConfirmation()
    {
        IsApplyConfirmationVisible = false;
        StatusMessage = "Aplicación del script cancelada.";
    }

    /// <summary>Runs the generated script against the target database and re-compares.</summary>
    public async Task ConfirmApplyScriptAsync()
    {
        IsApplyConfirmationVisible = false;

        var target = Target.ToConnectionInfo();
        if (!ValidateConnection(target, "destino"))
            return;

        IsBusy = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusMessage = "Aplicando script…";

        try
        {
            var result = await _executionService.ExecuteAsync(target, Script, cts.Token);

            if (!result.Succeeded)
            {
                StatusMessage = result.Summary;
                AddLog("Error", result.Error);
                return;
            }

            AddLog("Apply", result.Summary);
            StatusMessage = "Script aplicado. Volviendo a comparar…";

            // Re-comparing right away turns "the script ran" into "the databases now match".
            var source = Source.ToConnectionInfo();
            ClearResults();
            await RunComparisonAsync(source, target, cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Aplicación del script cancelada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al aplicar el script: {ex.Message}";
            AddLog("Error", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _cts = null;

            cts.Dispose();
            IsBusy = false;
        }
    }

    /// <summary>Dispatches to the comparer for the current mode. Caller owns the busy state.</summary>
    private Task RunComparisonAsync(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        CancellationToken cancellationToken)
        => Mode == ComparisonMode.Schema
            ? RunSchemaComparisonAsync(source, target, cancellationToken)
            : RunDataComparisonAsync(source, target, cancellationToken);

    private async Task RunSchemaComparisonAsync(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        CancellationToken cancellationToken)
    {
        var session = await Task.Run(
            () => _schemaComparer.Compare(source, target, _options, cancellationToken),
            cancellationToken);

        var result = session.Result;
        _session = session;
        _sessionDropObjectsNotInSource = _options.DropObjectsNotInSource;
        SchemaDifferences.AddRange(result.Differences);
        _excludedObjects.Clear();
        foreach (var node in SchemaDifferences)
            _excludedObjects.Add(node.Root);

        foreach (var error in result.Errors)
            AddLog("Error", error);

        foreach (var warning in result.Warnings)
            AddLog("Aviso", warning);

        Summary = result.Summary;
        Script = string.Empty;
        StatusMessage = result.IsEqual
            ? $"Sin diferencias de esquema ({result.Duration.TotalSeconds:0.0}s)."
            : $"Comparación de esquema completada en {result.Duration.TotalSeconds:0.0}s.";
        AddLog("Schema", result.IsEqual
            ? $"Sin diferencias de esquema en {result.Duration.TotalSeconds:0.0}s."
            : $"{result.TotalObjectCount} objeto(s) con diferencias · 0 seleccionado(s). Elige los objetos que quieres incluir en el script final.");
        Notify();
    }

    private async Task RunDataComparisonAsync(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo target,
        CancellationToken cancellationToken)
    {
        var result = await Task.Run(
            () => _dataComparer.Compare(source, target, _options, cancellationToken),
            cancellationToken);

        TableDifferences.AddRange(result.Tables);
        _selectedRows.Clear();

        foreach (var skipped in result.SkippedTables)
            AddLog("Omitida", skipped);

        foreach (var error in result.Errors)
            AddLog("Error", error);

        foreach (var warning in result.Warnings)
            AddLog("Aviso", warning);

        Summary = result.Summary;
        Script = string.Empty;
        StatusMessage = $"Comparación de datos completada en {result.Duration.TotalSeconds:0.0}s.";
        AddLog("Data", $"{result.InsertCount} inserciones · {result.UpdateCount} actualizaciones · {result.DeleteCount} eliminaciones disponibles en {result.Tables.Count} tabla(s). Ningún registro seleccionado.");
        Notify();
    }

    private bool ValidateConnection(DatabaseConnectionInfo info, string side)
    {
        if (string.IsNullOrWhiteSpace(info.ServerName) || string.IsNullOrWhiteSpace(info.DatabaseName))
        {
            StatusMessage = $"Falta el servidor o la base de datos de {side}.";
            return false;
        }

        if (!info.UseIntegratedSecurity && string.IsNullOrEmpty(info.Password))
        {
            StatusMessage = $"Falta la contraseña de {side}.";
            return false;
        }

        return true;
    }

    private void ClearResults()
    {
        _session = null;
        _sessionDropObjectsNotInSource = false;
        _excludedObjects.Clear();
        _collapsedGroups.Clear();
        _searchTerm = string.Empty;
        _focusedObject = null;
        _scriptView = SchemaScriptView.Final;
        SchemaDifferences.Clear();
        TableDifferences.Clear();
        _selectedRows.Clear();
        Summary = string.Empty;
        Script = string.Empty;
        Notify();
    }

    /// <summary>Lets child components (connection panels) ask the shell to re-render.</summary>
    public void Refresh() => Notify();

    private void Notify() => Changed?.Invoke();
}
