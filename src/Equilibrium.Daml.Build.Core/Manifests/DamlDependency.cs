using System;

namespace Equilibrium.Daml.Build.Core.Manifests;

/// <summary>An entry of <c>dependencies</c> or <c>data-dependencies</c>.</summary>
/// <param name="Text">As the manifest writes it.</param>
/// <param name="DarPath">The full path for a local DAR or DALF; null for built-ins, OCI locations and artifact aliases.</param>
public sealed record DamlDependency(string Text, string? DarPath)
{
    /// <summary>Whether DPM interprets this dependency as a local DAR path.</summary>
    public static bool IsLocalDar(string value) =>
        value is not null && !value.Contains("://") && value.EndsWith(".dar", StringComparison.Ordinal);

    /// <summary>Local archives accepted by DPM or damlc's legacy dependency resolution.</summary>
    public static bool IsLocalArchive(string value) =>
        IsLocalDar(value) || (value is not null && !value.Contains("://") && !value.StartsWith("@", StringComparison.Ordinal)
            && value.EndsWith(".dalf", StringComparison.Ordinal));
}
