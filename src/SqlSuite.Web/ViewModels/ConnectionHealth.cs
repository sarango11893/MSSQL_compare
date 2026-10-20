namespace SqlSuite.Web.ViewModels;

/// <summary>Reachability of one endpoint, shown as the coloured dot of the container status card.</summary>
public enum ConnectionHealth
{
    Unknown,
    Online,
    Offline,
}

public static class ConnectionHealthExtensions
{
    public static string Label(this ConnectionHealth health) => health switch
    {
        ConnectionHealth.Online => "ONLINE",
        ConnectionHealth.Offline => "OFFLINE",
        _ => "SIN PROBAR",
    };

    public static string CssClass(this ConnectionHealth health) => health switch
    {
        ConnectionHealth.Online => "online",
        ConnectionHealth.Offline => "offline",
        _ => "unknown",
    };
}
