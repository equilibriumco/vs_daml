using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Shouldly;
using VerifyXunit;
using Xunit;

namespace Daml.Build.Tests.Contract;

// The MSBuild packages' contract in one reviewable place: what users can set, what the build
// makes of the sample projects, the codes it reports and the targets the packages declare. A
// change to any of it shows up as a snapshot diff.
public class MSBuildContractTests
{
    private const string ProjectFolder = @"C:\work\App";

    [Fact, Trait("Category", "Unit")]
    public Task The_contract_at_a_glance()
    {
        var text = new StringBuilder();
        text.AppendLine("Properties");
        foreach (var property in DamlProperties.All)
        {
            Line(text, $"  {property.Scope,-13} {property.Name} = '{property.Default}' ({property.Allowed})");
        }
        text.AppendLine().AppendLine("Diagnostic codes");
        foreach (var (code, name) in Codes())
        {
            Line(text, $"  {code} {name}");
        }
        Targets(text, "Daml.Sdk targets", MSBuildTargetGraph.DamlSdkHooks, MSBuildTargetGraph.DamlSdk);
        Targets(text, "Daml.Build.CSharp targets", MSBuildTargetGraph.CSharpHooks, MSBuildTargetGraph.CSharp);
        return Verifier.Verify(text.ToString());
    }

    [Fact, Trait("Category", "Unit")]
    public Task What_the_build_makes_of_the_sample_projects()
    {
        var text = new StringBuilder();
        foreach (var sample in SampleProject.All())
        {
            Line(text, $"== {sample.Name}");
            var errors = new List<ConfigurationError>();
            var tools = ToolOptions.Read(name => Property(sample, name), errors);
            var codegen = CodegenOptions.Read(name => Property(sample, name), errors);
            // With an unusable pin the references are still checked, so one build reports everything.
            var digest = codegen.Component?.Digest ?? CodegenComponentPin.TryParse(DamlProperties.CodegenComponent.Default)!.Digest;
            var generations = GenerationReferences.Resolve(sample.References, ProjectFolder, digest, errors);

            Line(text, $"tools: dpm={tools.DpmPath ?? "(look for it)"} offline={tools.Offline} install={tools.AllowToolInstall} skip-preflight={tools.SkipPreflight}");
            Line(text, $"timeouts: process={Seconds(tools.ProcessTimeout)} lock={Seconds(tools.LockTimeout)}");
            Line(text, $"codegen: enabled={codegen.Enabled} mode={codegen.OutputMode} root={codegen.GeneratedRoot ?? "(none)"} java={codegen.JavaHome ?? "(PATH)"}");
            Line(text, $"generator: {codegen.Component?.Reference ?? "(none)"}");
            foreach (var generation in generations)
            {
                Line(text, $"generation: {generation.Source} {generation.Path} namespace={generation.Namespace ?? "(none)"} -> {generation.FolderName}");
            }
            foreach (var error in errors)
            {
                Line(text, $"error {error.Code}: {error.Message}");
            }
            text.AppendLine();
        }
        return Verifier.Verify(text.ToString());
    }

    // The planned order from project references to the compiler, and the targeted build that
    // refreshes bindings walks the same steps.
    [Fact, Trait("Category", "Unit")]
    public void Generated_code_reaches_the_compiler_through_one_chain_of_targets()
    {
        var dependsOn = MSBuildTargetGraph.CSharp.ToDictionary(t => t.Target, t => t.DependsOn);
        var chain = new List<string> { "DamlCollectGeneratedCompile" };
        while (dependsOn.TryGetValue(chain[0], out var next) && next.Length == 1)
        {
            chain.Insert(0, next[0]);
        }

        chain.ShouldBe(["ResolveProjectReferences", "DamlResolveDars", "DamlBuildMissingDars", "DamlComputeGenerationState", "DamlGenerateCSharp", "DamlCollectGeneratedCompile"]);
        MSBuildTargetGraph.CSharpHooks.ShouldContain(new HookContract("CoreCompileDependsOn", "DamlCollectGeneratedCompile", First: true));
        dependsOn["DamlRefreshBindings"].ShouldBe(["ResolveReferences", "DamlBuildReferences", .. chain.Skip(1)]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_target_depended_on_is_declared_or_belongs_to_msbuild()
    {
        string[] msbuild = ["ResolveProjectReferences", "ResolveReferences"];
        foreach (var graph in new[] { MSBuildTargetGraph.DamlSdk, MSBuildTargetGraph.CSharp })
        {
            var declared = graph.Select(t => t.Target).ToList();
            declared.ShouldBeUnique();
            graph.SelectMany(t => t.DependsOn).ShouldAllBe(d => declared.Contains(d) || msbuild.Contains(d));
        }
        MSBuildTargetGraph.DamlSdkHooks.Select(h => h.Target).ShouldAllBe(t => MSBuildTargetGraph.DamlSdk.Any(d => d.Target == t));
        MSBuildTargetGraph.CSharpHooks.Select(h => h.Target).ShouldAllBe(t => MSBuildTargetGraph.CSharp.Any(d => d.Target == t));
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_code_is_DAML_and_four_digits_and_used_once()
    {
        var codes = Codes().Select(c => c.Code).ToList();

        codes.ShouldBeUnique();
        codes.ShouldAllBe(c => Regex.IsMatch(c, "^DAML[0-9]{4}$"));
    }

    private static IEnumerable<(string Code, string Name)> Codes() =>
        typeof(DamlDiagnosticCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => ((string)f.GetRawConstantValue()!, f.Name))
            .OrderBy(c => c.Item1, StringComparer.Ordinal);

    private static string? Property(SampleProject sample, string name) => name switch
    {
        "MSBuildProjectDirectory" => ProjectFolder,
        "IntermediateOutputPath" => $@"obj\Debug\{sample.Properties["TargetFramework"]}\",
        _ => sample.Properties.TryGetValue(name, out var value) ? value : null,
    };

    private static string Seconds(TimeSpan? span) => span is { } value ? value.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s" : "none";

    private static void Line(StringBuilder text, FormattableString line) => text.AppendLine(line.ToString(CultureInfo.InvariantCulture));

    private static void Targets(StringBuilder text, string title, IReadOnlyList<HookContract> hooks, IReadOnlyList<TargetContract> targets)
    {
        text.AppendLine().AppendLine(title);
        foreach (var hook in hooks)
        {
            text.AppendLine(hook.First ? $"  {hook.List} = {hook.Target};$({hook.List})" : $"  {hook.List} += {hook.Target}");
        }
        foreach (var target in targets)
        {
            text.AppendLine(target.DependsOn.Length == 0 ? $"  {target.Target}" : $"  {target.Target} <- {string.Join("; ", target.DependsOn)}");
        }
    }
}
