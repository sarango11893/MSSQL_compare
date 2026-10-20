using Microsoft.Data.SqlClient;

namespace SqlSuite.Core.Models;

/// <summary>
/// Describes how to reach a single SQL Server database.
/// Keeps the property names from the original project template while building the
/// connection string through <see cref="SqlConnectionStringBuilder"/> so that
/// passwords containing <c>;</c>, quotes or braces cannot corrupt the string.
/// </summary>
public sealed class DatabaseConnectionInfo
{
    public string ServerName { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    public bool UseIntegratedSecurity { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Trust the server certificate (required by most local / container instances).</summary>
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Encryption mode passed to the driver. Defaults to <see cref="SqlConnectionEncryptOption.Optional"/>.</summary>
    public SqlConnectionEncryptOption Encrypt { get; set; } = SqlConnectionEncryptOption.Optional;

    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 30;

    public string GetConnectionString()
    {
        SqlConnectionStringBuilder builder = new()
        {
            DataSource = ServerName,
            InitialCatalog = DatabaseName,
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = Encrypt,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SqlSuite",
        };

        if (UseIntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = Username;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>Same connection string but pointed at another database on the same server.</summary>
    public string GetConnectionString(string databaseName)
    {
        SqlConnectionStringBuilder builder = new(GetConnectionString())
        {
            InitialCatalog = databaseName,
        };

        return builder.ConnectionString;
    }

    /// <summary>Connection string targeting <c>master</c>, used to list databases.</summary>
    public string GetMasterConnectionString() => GetConnectionString("master");

    /// <summary>Human readable label such as <c>server/database</c>, safe for logging (never exposes the password).</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(DatabaseName) ? ServerName : $"{ServerName}/{DatabaseName}";
}
