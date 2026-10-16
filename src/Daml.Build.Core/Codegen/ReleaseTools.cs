using System;
using Daml.Build.Core.Configuration;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// What this release was tested with, in one place: the generator it pins, the runtime its code
/// is for and the oldest tools it runs on. A new generator or runtime line is a new release.
/// </summary>
public static class ReleaseTools
{
    /// <summary>The version of the generation contract; a change regenerates every generation in place.</summary>
    public const int GenerationContract = 1;

    /// <summary>The oldest JDK the generator's DAR reader runs on.</summary>
    public const int MinimumJava = 17;

    /// <summary>The C# code generator, pinned by digest.</summary>
    public static CodegenComponentPin CodegenComponent { get; } =
        CodegenComponentPin.TryParse("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f040219936298f085e2ade77fbbc37829df065b879633282aa4628c9a1b6548")!;

    /// <summary>The runtime package the generated code is written for, and its major.minor line.</summary>
    public static (string Package, Version Line) Runtime { get; } = ("Daml.Runtime", new Version(0, 6));

    /// <summary>The oldest .NET a project using the bindings may target; the runtime supports nothing older.</summary>
    public static Version MinimumTargetFramework { get; } = new(10, 0);

    /// <summary>The oldest dpm the build was tested with.</summary>
    public static Version MinimumDpm { get; } = new(1, 0, 22);
}
