namespace SqlSuite.Web.ViewModels;

/// <summary>One line of the execution log rendered in the console panel.</summary>
public sealed record LogEntry(DateTime Timestamp, string Category, string Message)
{
    /// <summary>Timestamp in the format used by the log console, for example <c>3:33:27 a.m.</c>.</summary>
    public string Time => Timestamp.ToString("h:mm:ss tt", System.Globalization.CultureInfo.CurrentCulture);
}
