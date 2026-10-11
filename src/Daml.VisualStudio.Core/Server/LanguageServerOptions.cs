namespace Daml.VisualStudio.Core.Server;

/// <summary>User settings that shape how the language server is found and started.</summary>
public sealed record LanguageServerOptions
{
    /// <summary>Explicit path to dpm; empty means search PATH and the install folder.</summary>
    public string? DpmPath { get; init; }

    /// <summary>Explicit path to the daml assistant; empty means search PATH and the install folder.</summary>
    public string? DamlAssistantPath { get; init; }
}
