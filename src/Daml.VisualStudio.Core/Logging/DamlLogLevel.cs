namespace Daml.VisualStudio.Core.Logging;

/// <summary>Severity of a log entry, from most to least verbose.</summary>
public enum DamlLogLevel
{
    /// <summary>Wire-level detail, e.g. individual JSON-RPC messages.</summary>
    Trace,

    /// <summary>Diagnostics useful when investigating a problem.</summary>
    Debug,

    /// <summary>Normal lifecycle events.</summary>
    Information,

    /// <summary>Something went wrong but the extension carried on.</summary>
    Warning,

    /// <summary>A failure the user is likely to notice.</summary>
    Error,
}
