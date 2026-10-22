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

// The Equilibrium.Daml.Sdk files as shipped, held to the target graph and the properties of the contract.
public class DamlSdkFilesTests
{
    private static readonly string SdkFolder = Path.Combine(Repository.Root, @"src\Equilibrium.Daml.Sdk\Sdk");

    private static XDocument Load(string name) => XDocument.Load(Path.Combine(SdkFolder, name), LoadOptions.SetLineInfo);

    private static List<XElement> Targets() => Load("Sdk.targets").Root!.Elements("Target").ToList();

    private static string[] Split(string? list) =>
        (list ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    [Fact, Trait("Category", "Unit")]
    public void The_targets_declare_every_contract_target_with_its_dependencies_and_nothing_more()
    {
        var targets = Targets();

        foreach (var contract in MSBuildTargetGraph.DamlSdk)
        {
            var target = targets.SingleOrDefault(t => (string?)t.Attribute("Name") == contract.Target).ShouldNotBeNull(contract.Target);
            Split((string?)target.Attribute("DependsOnTargets")).ShouldBe(contract.DependsOn, contract.Target);
        }
        targets.Select(t => (string)t.Attribute("Name")!).Where(n => n[0] != '_')
            .ShouldBe(MSBuildTargetGraph.DamlSdk.Select(t => t.Target), ignoreOrder: true);
    }

    // A target's condition is judged before its dependencies run (S-09).
    [Fact, Trait("Category", "Unit")]
    public void No_target_has_a_condition()
    {
        Targets().Where(t => t.Attribute("Condition") is not null).Select(t => (string)t.Attribute("Name")!).ShouldBeEmpty();
        Targets().Where(t => t.Attribute("BeforeTargets") is not null || t.Attribute("AfterTargets") is not null)
            .Select(t => (string)t.Attribute("Name")!).ShouldBeEmpty();
    }

    // NoTargets assigns CoreBuildDependsOn outright at the end of its own targets (S-06).
    [Fact, Trait("Category", "Unit")]
    public void The_build_lists_are_joined_only_after_the_NoTargets_targets_are_imported()
    {
        var root = Load("Sdk.targets").Root!;
        var import = root.Elements("Import").ShouldHaveSingleItem();
        ((string?)import.Attribute("Sdk"), (string?)import.Attribute("Project")).ShouldBe(("Microsoft.Build.NoTargets", "Sdk.targets"));

        foreach (var hook in MSBuildTargetGraph.DamlSdkHooks)
        {
            var assignment = root.Elements("PropertyGroup").Elements(hook.List).ShouldHaveSingleItem(hook.List);
            assignment.Value.ShouldBe(hook.First ? $"{hook.Target};$({hook.List})" : $"$({hook.List});{hook.Target}");
            assignment.IsAfter(import).ShouldBeTrue($"{hook.List} is joined before NoTargets is imported");
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void The_props_import_NoTargets_first_and_give_the_Daml_project_properties_their_defaults()
    {
        var root = Load("Sdk.props").Root!;
        var first = root.Elements().First();
        (first.Name.LocalName, (string?)first.Attribute("Sdk")).ShouldBe(("Import", "Microsoft.Build.NoTargets"));

        foreach (var property in DamlProperties.All.Where(p => p.Scope == DamlPropertyScope.DamlProject))
        {
            var element = root.Elements("PropertyGroup").Elements(property.Name).ShouldHaveSingleItem(property.Name);
            element.Value.ShouldBe(property.Default);
            ((string?)element.Attribute("Condition")).ShouldBe($"'$({property.Name})' == ''");
        }
    }

    // Every task comes from the package's own tasks folder and exists there.
    [Fact, Trait("Category", "Unit")]
    public void Every_task_the_targets_run_is_one_of_the_shipped_tasks()
    {
        var root = Load("Sdk.targets").Root!;
        var declared = root.Elements("UsingTask").ToList();
        declared.ShouldAllBe(u => (string?)u.Attribute("AssemblyFile") == "$(_EqDamlSdkTasks)");
        var tasks = typeof(Equilibrium.Daml.Build.Tasks.ReadDamlManifest).Assembly;
        foreach (var name in declared.Select(u => (string)u.Attribute("TaskName")!))
        {
            tasks.GetType(name).ShouldNotBeNull(name);
        }
        var used = root.Elements("Target").Elements().Select(e => e.Name.LocalName).Where(n => n.StartsWith("Daml", StringComparison.Ordinal) || n == "ReadDamlManifest");
        used.ShouldAllBe(n => declared.Any(u => ((string)u.Attribute("TaskName")!).EndsWith("." + n, StringComparison.Ordinal)));
    }
}
