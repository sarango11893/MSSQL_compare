using SqlSuite.Core.Models;
using SqlSuite.Core.Services;

namespace SqlSuite.Web.ViewModels;

/// <summary>
/// Editable connection settings for one side of the comparison (source or target).
/// Builds the <see cref="DatabaseConnectionInfo"/> consumed by the Core services.
/// </summary>
public sealed class ConnectionState
{
    private readonly DatabaseConnectionService _connectionService = new();

    public ConnectionState(string title) => Title = title;

    public string Title { get; }

    public List<string> Databases { get; } = new();

    public string ServerName { get; set; } = "localhost";

    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Windows/Entra authentication is rarely available from a web host; SQL auth is the default.</summary>
    public bool UseIntegratedSecurity { get; set; }

    public string Username { get; set; } = "sa";

    /// <summary>Kept in memory only for the lifetime of the circuit; never persisted.</summary>
    public string Password { get; set; } = string.Empty;

    public string StatusMessage { get; private set; } = string.Empty;

    public bool IsBusy { get; private set; }

    /// <summary>Last known reachability of the endpoint; drives the container status card.</summary>
    public ConnectionHealth Health { get; private set; } = ConnectionHealth.Unknown;

    /// <summary>Endpoint shown in the source/target chip, for example <c>localhost:14331 / SqlSuiteDemo</c>.</summary>
    public string EndpointLabel => string.IsNullOrWhiteSpace(DatabaseName)
        ? ServerName
        : $"{ServerName} / {DatabaseName}";

    public void MarkOnline() => Health = ConnectionHealth.Online;

    public void MarkOffline() => Health = ConnectionHealth.Offline;

    public bool CanConnect => !string.IsNullOrWhiteSpace(ServerName);

    public DatabaseConnectionInfo ToConnectionInfo() => new()
    {
        ServerName = ServerName.Trim(),
        DatabaseName = DatabaseName.Trim(),
        UseIntegratedSecurity = UseIntegratedSecurity,
        Username = Username.Trim(),
        Password = Password,
    };

    public async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusMessage = "Conectando…";

        try
        {
            var info = ToConnectionInfo();
            var database = string.IsNullOrWhiteSpace(info.DatabaseName) ? "master" : info.DatabaseName;
            var version = await _connectionService.TestConnectionAsync(info, database);
            StatusMessage = $"OK · {version}";
            Health = ConnectionHealth.Online;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error · {ex.Message}";
            Health = ConnectionHealth.Offline;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadDatabasesAsync()
    {
        IsBusy = true;
        StatusMessage = "Leyendo bases de datos…";

        try
        {
            var databases = await _connectionService.ListDatabasesAsync(ToConnectionInfo());

            Databases.Clear();
            Databases.AddRange(databases.Select(d => d.Name));

            StatusMessage = $"{Databases.Count} base(s) de datos encontrada(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error · {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
