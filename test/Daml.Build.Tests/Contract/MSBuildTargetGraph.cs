using System.Collections.Generic;

namespace Daml.Build.Tests.Contract;

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
        new("CoreBuildDependsOn", "DamlBuild"),
        new("CleanDependsOn", "DamlClean"),
        new("CollectUpToDateCheckInputDesignTimeDependsOn", "DamlCollectUpToDateCheckItems"),
        new("CollectUpToDateCheckBuiltDesignTimeDependsOn", "DamlCollectUpToDateCheckItems"),
    ];

    public static IReadOnlyList<TargetContract> DamlSdk { get; } =
    [
        new("DamlResolveManifest"),
        new("DamlComputeBuildState", "DamlResolveManifest"),
        new("DamlBuild", "DamlResolveManifest", "DamlComputeBuildState"),
        new("GetDamlDar", "DamlResolveManifest"),
        new("DamlTest", "DamlBuild"),
        new("DamlClean", "DamlResolveManifest"),
        new("DamlCollectUpToDateCheckItems", "DamlResolveManifest"),
    ];

    public static IReadOnlyList<HookContract> CSharpHooks { get; } =
    [
        new("CoreCompileDependsOn", "DamlCollectGeneratedCompile", First: true),
        new("CleanDependsOn", "DamlCleanGenerated"),
        new("CollectUpToDateCheckInputDesignTimeDependsOn", "DamlCollectUpToDateCheckItems"),
        new("CollectUpToDateCheckBuiltDesignTimeDependsOn", "DamlCollectUpToDateCheckItems"),
    ];

    public static IReadOnlyList<TargetContract> CSharp { get; } =
    [
        new("DamlResolveDars", "ResolveProjectReferences"),
        new("DamlComputeGenerationState", "DamlResolveDars"),
        new("DamlGenerateCSharp", "DamlComputeGenerationState"),
        new("DamlCollectGeneratedCompile", "DamlGenerateCSharp"),
        new("DamlBuildReferences", "ResolveProjectReferences"),
        new("DamlRefreshBindings", "ResolveReferences", "DamlBuildReferences", "DamlResolveDars", "DamlComputeGenerationState", "DamlGenerateCSharp", "DamlCollectGeneratedCompile"),
        new("DamlCleanGenerated"),
        new("DamlCollectUpToDateCheckItems", "DamlResolveDars"),
    ];
}
