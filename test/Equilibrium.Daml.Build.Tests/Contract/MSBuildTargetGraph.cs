using System.Collections.Generic;

namespace Equilibrium.Daml.Build.Tests.Contract;

/// <summary>A target and the targets it names in <c>DependsOnTargets</c>, in order.</summary>
internal sealed record TargetContract(string Target, params string[] DependsOn);

/// <summary>
/// A target put into one of MSBuild's <c>*DependsOn</c> lists: at the front of it, or at the end
/// (always after the import that last assigns the list).
/// </summary>
internal sealed record HookContract(string List, string Target, bool First = false);

/// <summary>
/// The targets the two packages have to declare, for their .targets files to be checked against.
/// Changing it changes the contract snapshot too.
/// </summary>
internal static class MSBuildTargetGraph
{
    public static IReadOnlyList<HookContract> DamlSdkHooks { get; } =
    [
        new("CoreBuildDependsOn", "EqDamlBuild"),
        new("CleanDependsOn", "EqDamlClean"),
        new("CollectUpToDateCheckInputDesignTimeDependsOn", "EqDamlCollectUpToDateCheckItems"),
        new("CollectUpToDateCheckBuiltDesignTimeDependsOn", "EqDamlCollectUpToDateCheckItems"),
    ];

    public static IReadOnlyList<TargetContract> DamlSdk { get; } =
    [
        new("EqDamlResolveManifest"),
        new("EqDamlComputeBuildState", "EqDamlResolveManifest"),
        new("EqDamlBuild", "EqDamlResolveManifest", "EqDamlComputeBuildState"),
        new("EqDamlGetDar", "EqDamlResolveManifest"),
        new("EqDamlTest", "EqDamlBuild"),
        new("EqDamlClean", "EqDamlResolveManifest"),
        new("EqDamlCollectUpToDateCheckItems", "EqDamlResolveManifest"),
    ];

    public static IReadOnlyList<HookContract> CSharpHooks { get; } =
    [
        new("CoreCompileDependsOn", "EqDamlCollectGeneratedCompile", First: true),
        new("CleanDependsOn", "EqDamlCleanGenerated"),
        new("CollectUpToDateCheckInputDesignTimeDependsOn", "EqDamlCollectUpToDateCheckItems"),
        new("CollectUpToDateCheckBuiltDesignTimeDependsOn", "EqDamlCollectUpToDateCheckItems"),
    ];

    public static IReadOnlyList<TargetContract> CSharp { get; } =
    [
        new("EqDamlResolveDars", "ResolveProjectReferences"),
        new("EqDamlEnsureDars", "EqDamlResolveDars"),
        new("EqDamlComputeGenerationState", "EqDamlEnsureDars"),
        new("EqDamlGenerateCSharp", "EqDamlComputeGenerationState"),
        new("EqDamlCollectGeneratedCompile", "EqDamlGenerateCSharp"),
        new("EqDamlBuildReferences", "ResolveProjectReferences"),
        new("EqDamlRefreshBindings", "ResolveReferences", "EqDamlBuildReferences", "EqDamlResolveDars", "EqDamlEnsureDars", "EqDamlComputeGenerationState", "EqDamlGenerateCSharp", "EqDamlCollectGeneratedCompile"),
        new("EqDamlCleanGenerated"),
        new("EqDamlCollectUpToDateCheckItems", "EqDamlResolveDars"),
    ];
}
