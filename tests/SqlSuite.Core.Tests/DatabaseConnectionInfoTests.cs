using Microsoft.Data.SqlClient;
using SqlSuite.Core.Models;

namespace SqlSuite.Core.Tests;

public sealed class DatabaseConnectionInfoTests
{
    private static SqlConnectionStringBuilder Parse(string connectionString) => new(connectionString);

    [Fact]
    public void IntegratedSecurity_ProducesTrustedConnection()
    {
        var info = new DatabaseConnectionInfo
        {
            ServerName = "localhost",
            DatabaseName = "SqlSuiteDemo",
        };

        var builder = Parse(info.GetConnectionString());

        Assert.Equal("localhost", builder.DataSource);
        Assert.Equal("SqlSuiteDemo", builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void SqlAuthentication_IncludesCredentials()
    {
        var info = new DatabaseConnectionInfo
        {
            ServerName = "127.0.0.1,14331",
            DatabaseName = "SqlSuiteDemo",
            UseIntegratedSecurity = false,
            Username = "sa",
            Password = "Example-Only-Password-123!",
        };

        var builder = Parse(info.GetConnectionString());

        Assert.Equal("127.0.0.1,14331", builder.DataSource);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("sa", builder.UserID);
        Assert.Equal("Example-Only-Password-123!", builder.Password);
    }

    [Fact]
    public void GetConnectionString_WithDatabaseName_OverridesCatalog()
    {
        var info = new DatabaseConnectionInfo { ServerName = "srv", DatabaseName = "original" };

        Assert.Equal("master", Parse(info.GetConnectionString("master")).InitialCatalog);
    }

    [Fact]
    public void MasterConnectionString_UsesMasterCatalog()
    {
        var info = new DatabaseConnectionInfo { ServerName = "srv", DatabaseName = "Sales" };

        Assert.Equal("master", Parse(info.GetMasterConnectionString()).InitialCatalog);
    }

    [Fact]
    public void ToString_NeverLeaksPassword()
    {
        var info = new DatabaseConnectionInfo
        {
            ServerName = "srv",
            DatabaseName = "db",
            UseIntegratedSecurity = false,
            Username = "sa",
            Password = "SuperSecret1",
        };

        Assert.DoesNotContain("SuperSecret1", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetConnectionString_RoundTripsSemicolonInPassword()
    {
        var info = new DatabaseConnectionInfo
        {
            ServerName = "srv",
            DatabaseName = "db",
            UseIntegratedSecurity = false,
            Username = "sa",
            Password = "a;b",
        };

        Assert.Equal("a;b", Parse(info.GetConnectionString()).Password);
    }
}
