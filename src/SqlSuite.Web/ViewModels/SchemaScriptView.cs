namespace SqlSuite.Web.ViewModels;

/// <summary>Which script the panel shows: the focused object only, or the whole deployment.</summary>
public enum SchemaScriptView
{
    /// <summary>Script of the single object the user clicked in the tree.</summary>
    Object,

    /// <summary>Combined script with every selected object.</summary>
    Final,
}
