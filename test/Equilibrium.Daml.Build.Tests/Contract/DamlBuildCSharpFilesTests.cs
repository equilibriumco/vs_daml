using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Contract;

// The Daml.Build.CSharp build files as shipped, held to the target graph and the properties of the contract.
public class DamlBuildCSharpFilesTests
{
    private static readonly string BuildFolder = Path.Combine(Repository.Root, @"src\Daml.Build.CSharp\build");

    private static XElement Load(string extension) => XDocument.Load(Path.Combine(BuildFolder, "Daml.Build.CSharp" + extension)).Root!;

    private static List<XElement> Targets() => Load(".targets").Elements("Target").ToList();

    private static string[] Split(string? list) =>
        (list ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    [Fact, Trait("Category", "Unit")]
    public void The_targets_declare_every_contract_target_with_its_dependencies_and_nothing_more()
    {
        var targets = Targets();

        foreach (var contract in MSBuildTargetGraph.CSharp)
        {
            var target = targets.SingleOrDefault(t => (string?)t.Attribute("Name") == contract.Target).ShouldNotBeNull(contract.Target);
            Split((string?)target.Attribute("DependsOnTargets")).ShouldBe(contract.DependsOn, contract.Target);
        }
        targets.Select(t => (string)t.Attribute("Name")!).Where(n => n[0] != '_')
            .ShouldBe(MSBuildTargetGraph.CSharp.Select(t => t.Target), ignoreOrder: true);
    }

    // A target's condition is judged before its dependencies run, so it can't depend on what they find.
    [Fact, Trait("Category", "Unit")]
    public void No_target_has_a_condition_or_hangs_on_another_target()
    {
        Targets().Where(t => t.Attribute("Condition") is not null || t.Attribute("BeforeTargets") is not null || t.Attribute("AfterTargets") is not null)
            .Select(t => (string)t.Attribute("Name")!).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void The_build_lists_are_joined_as_the_contract_says()
    {
        var root = Load(".targets");

        foreach (var hook in MSBuildTargetGraph.CSharpHooks)
        {
            root.Elements("PropertyGroup").Elements(hook.List).ShouldHaveSingleItem(hook.List)
                .Value.ShouldBe(hook.First ? $"{hook.Target};$({hook.List})" : $"$({hook.List});{hook.Target}");
        }
    }

    // Settings with a value of their own; DamlGeneratedDir depends on the mode and is worked out later.
    [Fact, Trait("Category", "Unit")]
    public void The_props_give_the_CSharp_project_properties_their_defaults()
    {
        var root = Load(".props");

        foreach (var property in DamlProperties.All.Where(p => p.Scope == DamlPropertyScope.CSharpProject && p.Default.Length > 0))
        {
            var element = root.Elements("PropertyGroup").Elements(property.Name).ShouldHaveSingleItem(property.Name);
            element.Value.ShouldBe(property.Default);
            ((string?)element.Attribute("Condition")).ShouldBe($"'$({property.Name})' == ''");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_task_the_targets_run_is_one_of_the_shipped_tasks()
    {
        var root = Load(".targets");
        var declared = root.Elements("UsingTask").ToList();
        declared.ShouldAllBe(u => (string?)u.Attribute("AssemblyFile") == "$(_DamlCSharpTasks)");
        var tasks = typeof(Equilibrium.Daml.Build.Tasks.DamlCodegenCSharp).Assembly;
        foreach (var name in declared.Select(u => (string)u.Attribute("TaskName")!))
        {
            tasks.GetType(name).ShouldNotBeNull(name);
        }
        root.Elements("Target").Elements().Select(e => e.Name.LocalName).Where(n => n.StartsWith("Daml", StringComparison.Ordinal))
            .ShouldAllBe(n => declared.Any(u => ((string)u.Attribute("TaskName")!).EndsWith("." + n, StringComparison.Ordinal)));
    }
}
