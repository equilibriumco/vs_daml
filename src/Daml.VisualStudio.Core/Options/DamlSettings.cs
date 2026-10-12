using System;
using System.Collections.Generic;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Server;

namespace Daml.VisualStudio.Core.Options;

/// <summary>
/// One snapshot of the user's Daml settings, independent of where VS stores them. Turns them
/// into what the language server needs and says which edits only take effect after a restart.
/// </summary>
public sealed record DamlSettings
{
    /// <summary>Explicit path to dpm; blank means search PATH and the install folder.</summary>
    public string? DpmPath { get; init; }

    /// <summary>Explicit path to the daml assistant; blank means search PATH and the install folder.</summary>
    public string? DamlAssistantPath { get; init; }

    /// <summary>Run one <c>multi-ide</c> server for the whole folder rather than <c>ide</c> for one package.</summary>
    public bool UseMultiIde { get; init; } = true;

    /// <summary>How much the language server logs.</summary>
    public ServerLogLevel LogLevel { get; init; } = ServerLogLevel.Info;

    /// <summary>Extra damlc arguments, split on whitespace; double quotes group.</summary>
    public string? ExtraArguments { get; init; }

    /// <summary>Run every script in a file when it opens.</summary>
    public bool AutorunAllTests { get; init; }

    /// <summary>Ping the server regularly and restart it when it stops answering.</summary>
    public bool KeepAliveEnabled { get; init; } = true;

    /// <summary>The kind of server the settings ask for.</summary>
    public ServerMode ServerMode => UseMultiIde ? ServerMode.MultiIde : ServerMode.Ide;

    /// <summary>The settings the server command is built from; blank text counts as not set.</summary>
    public LanguageServerOptions ToLanguageServerOptions() => new()
    {
        DpmPath = Clean(DpmPath),
        DamlAssistantPath = Clean(DamlAssistantPath),
        LogLevel = LogLevel,
        ExtraArguments = Clean(ExtraArguments),
        AutorunAllTests = AutorunAllTests,
    };

    /// <summary>
    /// Names of the settings that differ between <paramref name="before"/> and
    /// <paramref name="after"/> and only reach a running server through a restart. Edits that
    /// leave the command the same, such as a path typed in another case, don't count.
    /// </summary>
    public static IReadOnlyList<string> ChangesNeedingRestart(DamlSettings before, DamlSettings after)
    {
        if (before is null)
        {
            throw new ArgumentNullException(nameof(before));
        }
        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        var changes = new List<string>();
        if (!SamePath(before.DpmPath, after.DpmPath))
        {
            changes.Add(nameof(DpmPath));
        }
        if (!SamePath(before.DamlAssistantPath, after.DamlAssistantPath))
        {
            changes.Add(nameof(DamlAssistantPath));
        }
        if (before.UseMultiIde != after.UseMultiIde)
        {
            changes.Add(nameof(UseMultiIde));
        }
        if (before.LogLevel != after.LogLevel)
        {
            changes.Add(nameof(LogLevel));
        }
        if (!string.Equals(Clean(before.ExtraArguments), Clean(after.ExtraArguments), StringComparison.Ordinal))
        {
            changes.Add(nameof(ExtraArguments));
        }
        if (before.AutorunAllTests != after.AutorunAllTests)
        {
            changes.Add(nameof(AutorunAllTests));
        }
        return changes;
    }

    // Windows paths ignore case.
    private static bool SamePath(string? a, string? b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
}
