using System;
using Equilibrium.Daml.Build.Core.Configuration;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// What this release was tested with, in one place: the generator it pins, the runtime its code
/// is for and the oldest tools it runs on. A new generator or runtime version is a new release.
/// </summary>
internal static class ReleaseTools
{
    /// <summary>The version of the generation contract; a change regenerates every generation in place.</summary>
    public const int GenerationContract = 2;

    /// <summary>The oldest JDK the generator's DAR reader runs on.</summary>
    public const int MinimumJava = 17;

    /// <summary>The C# code generator, pinned by digest.</summary>
    public static CodegenComponentPin CodegenComponent { get; } =
        CodegenComponentPin.TryParse("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.8.0@sha256:002f7382a01e141395d4cf62c6a42696c6778108c592444d9233ec31e26eeb38")!;

    /// <summary>The runtime package and exact version tested with the generated code.</summary>
    public static (string Package, Version Version) Runtime { get; } = ("Daml.Runtime", new Version(RuntimeVersion));

    /// <summary>The tested version of both Daml.Runtime and Daml.Ledger.Abstractions.</summary>
    public const string RuntimeVersion = "0.8.0";

    /// <summary>The oldest .NET a project using the bindings may target; the runtime supports nothing older.</summary>
    public static Version MinimumTargetFramework { get; } = new(10, 0);

    /// <summary>The oldest dpm the build was tested with.</summary>
    public static Version MinimumDpm { get; } = new(1, 0, 22);
}
