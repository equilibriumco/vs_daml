namespace Daml.VisualStudio.Core.Sdk;

/// <summary>The command-line tool that runs a Daml SDK's commands, damlc included.</summary>
public enum AssistantKind
{
    /// <summary>dpm, Digital Asset's package manager; runs SDK 3.4 and later.</summary>
    Dpm,

    /// <summary>The older <c>daml</c> assistant; needed for SDKs before 3.4.</summary>
    DamlAssistant,
}

/// <summary>A tool found on disk.</summary>
/// <param name="Kind">Which tool it is.</param>
/// <param name="Path">Full path, possibly to a <c>.cmd</c> wrapper.</param>
public sealed record AssistantLocation(AssistantKind Kind, string Path);
