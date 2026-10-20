using System.Collections.Generic;

namespace Daml.Build.Core.Configuration;

/// <summary>Every property the Daml packages read, with its default.</summary>
public static class DamlProperties
{
    private const string Boolean = "true or false";

    /// <summary>The dpm to run; empty searches PATH, then the dpm home directories.</summary>
    public static DamlProperty DpmPath { get; } = new("DamlDpmPath", DamlPropertyScope.Shared, "", "the path of dpm or a command on PATH");

    /// <summary>Never install or download anything; a missing tool is an error.</summary>
    public static DamlProperty Offline { get; } = new("DamlOffline", DamlPropertyScope.Shared, "false", Boolean);

    /// <summary>Let dpm install a missing Daml SDK or code generator.</summary>
    public static DamlProperty AllowToolInstall { get; } = new("DamlAllowToolInstall", DamlPropertyScope.Shared, "false", Boolean);

    /// <summary>Skip the tool version checks, at the user's own risk. Nothing else is skipped.</summary>
    public static DamlProperty SkipPreflight { get; } = new("DamlSkipPreflight", DamlPropertyScope.Shared, "false", Boolean);

    /// <summary>How long one dpm run may take before it's stopped.</summary>
    public static DamlProperty ProcessTimeoutSeconds { get; } =
        new("DamlProcessTimeoutSeconds", DamlPropertyScope.Shared, "1800", "a whole number of seconds, or 0 for no limit");

    /// <summary>How long a build waits for another build's lock on the generator cache or output.</summary>
    public static DamlProperty LockTimeoutSeconds { get; } =
        new("DamlLockTimeoutSeconds", DamlPropertyScope.Shared, "600", "a whole number of seconds from 1 up");

    /// <summary>The package's <c>daml.yaml</c>.</summary>
    public static DamlProperty ManifestPath { get; } =
        new("DamlManifestPath", DamlPropertyScope.DamlProject, @"$(MSBuildProjectDirectory)\daml.yaml", "the path of the package's daml.yaml");

    /// <summary>Whether building the project builds its DAR.</summary>
    public static DamlProperty BuildEnabled { get; } = new("DamlBuildEnabled", DamlPropertyScope.DamlProject, "true", Boolean);

    /// <summary>Whether the project generates and compiles bindings.</summary>
    public static DamlProperty CodegenEnabled { get; } = new("DamlCodegenEnabled", DamlPropertyScope.CSharpProject, "true", Boolean);

    /// <summary>The code generator, pinned by digest; the default is the one this release was tested with.</summary>
    public static DamlProperty CodegenComponent { get; } = new(
        "DamlCodegenComponent",
        DamlPropertyScope.CSharpProject,
        Codegen.ReleaseTools.CodegenComponent.Reference,
        "oci://<registry>/<repository>:<tag>@sha256:<64 lowercase hex digits>");

    /// <summary>Whether generated code goes under <c>obj</c> or into the project.</summary>
    public static DamlProperty GeneratedOutputMode { get; } =
        new("DamlGeneratedOutputMode", DamlPropertyScope.CSharpProject, "Intermediate", "Intermediate or Source");

    /// <summary>
    /// The folder that holds every generation; empty means <c>Daml</c> in the build's
    /// intermediate folder, or <c>Generated\Daml</c> in the project in Source mode.
    /// </summary>
    public static DamlProperty GeneratedDir { get; } =
        new("DamlGeneratedDir", DamlPropertyScope.CSharpProject, "", "a folder of its own");

    /// <summary>The JDK the generator runs on; empty uses java from PATH.</summary>
    public static DamlProperty JavaHome { get; } = new("DamlJavaHome", DamlPropertyScope.CSharpProject, "$(JAVA_HOME)", "a JDK folder");

    /// <summary>All of the above, shared ones first.</summary>
    public static IReadOnlyList<DamlProperty> All { get; } =
    [
        DpmPath, Offline, AllowToolInstall, SkipPreflight, ProcessTimeoutSeconds, LockTimeoutSeconds,
        ManifestPath, BuildEnabled,
        CodegenEnabled, CodegenComponent, GeneratedOutputMode, GeneratedDir, JavaHome,
    ];
}
