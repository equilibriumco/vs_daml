using System;
using System.Collections.Generic;
using System.Linq;

namespace Daml.VisualStudio.Core.Sdk;

/// <summary>The SDK a package's daml.yaml asks for.</summary>
/// <param name="PackageRoot">Package folder.</param>
/// <param name="Name">Package name, if the manifest has one.</param>
/// <param name="SdkVersion">The <c>sdk-version</c>, if set.</param>
/// <param name="Components">Component pins from the manifest.</param>
public sealed record PackageSdk(string PackageRoot, string? Name, string? SdkVersion, IReadOnlyList<string> Components);

/// <summary>
/// Which tool runs the workspace's language server, and what is known about the SDK versions
/// involved. These are kept apart on purpose: dpm's active SDK is only its global default, the
/// server's working folder may pin another, and every package may pin its own.
/// </summary>
public sealed record SdkResolution
{
    /// <summary>The tool to launch damlc through.</summary>
    public required AssistantLocation Assistant { get; init; }

    /// <summary>
    /// The <c>sdk-version</c> of the daml.yaml in the server's working folder; <c>null</c> when
    /// that folder has none, in which case the tool's own default runs the server.
    /// </summary>
    public string? ContextSdkVersion { get; init; }

    /// <summary>dpm's active (global default) SDK, for diagnostics only; <c>null</c> if unknown.</summary>
    public string? ActiveDpmVersion { get; init; }

    /// <summary>The SDK reported by <c>daml version</c> when the daml assistant was chosen.</summary>
    public string? AssistantSdkVersion { get; init; }

    /// <summary>
    /// The version of the damlc that will actually run the server (a component version, e.g. 3.6.2
    /// for SDK 3.6.0), as damlc reports it; <c>null</c> if it couldn't be asked.
    /// </summary>
    public string? CoordinatorVersion { get; init; }

    /// <summary>
    /// Whether <c>damlc multi-ide</c> is available: damlc's own answer, else a guess from the SDK
    /// version, else <c>null</c>.
    /// </summary>
    public bool? SupportsMultiIde { get; init; }

    /// <summary>Whether <c>multi-ide</c> accepts <c>--ide-identifier</c>; <c>null</c> if damlc couldn't be asked.</summary>
    public bool? SupportsIdeIdentifier { get; init; }

    /// <summary>Each workspace package's own SDK pin.</summary>
    public IReadOnlyList<PackageSdk> Packages { get; init; } = [];

    /// <summary>Things the user should know that don't stop the server, e.g. mixed or unknown versions.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when packages pin different SDK versions.</summary>
    public bool HasMixedSdkVersions =>
        Packages.Select(p => p.SdkVersion).Where(v => v is not null).Distinct(StringComparer.Ordinal).Skip(1).Any();
}
