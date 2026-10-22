namespace Equilibrium.Daml.VisualStudio.Core.Server;

/// <summary>How much the language server logs.</summary>
public enum ServerLogLevel
{
    /// <summary>Everything, including protocol detail.</summary>
    Debug,

    /// <summary>Normal operation messages.</summary>
    Info,

    /// <summary>Warnings and errors.</summary>
    Warning,

    /// <summary>Errors only.</summary>
    Error,
}

/// <summary>User settings that shape how the language server is found and started.</summary>
public sealed record LanguageServerOptions
{
    /// <summary>Explicit path to dpm; empty means search PATH and the install folder.</summary>
    public string? DpmPath { get; init; }

    /// <summary>Explicit path to the daml assistant; empty means search PATH and the install folder.</summary>
    public string? DamlAssistantPath { get; init; }

    /// <summary>Server log level. multi-ide takes all four; ide only distinguishes Debug.</summary>
    public ServerLogLevel LogLevel { get; init; } = ServerLogLevel.Info;

    /// <summary>Extra damlc arguments, split on whitespace; double quotes group.</summary>
    public string? ExtraArguments { get; init; }

    /// <summary>Run every script in a file when it opens.</summary>
    public bool AutorunAllTests { get; init; }
}
