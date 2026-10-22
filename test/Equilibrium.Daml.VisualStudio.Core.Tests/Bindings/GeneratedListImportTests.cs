using System;
using System.Linq;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Bindings;

// Visual Studio watches only the files an evaluation imported; a list of generated files written
// after an evaluation that found none is missed until the project is evaluated again.
public class GeneratedListImportTests
{
    private const string List = @"C:\sln\App\obj\daml\App.csproj.Debug.net10.0.g.props";

    private static readonly string[] Imports = [@"C:\Program Files\dotnet\sdk\10.0.402\Sdks\Microsoft.NET.Sdk\Sdk\Sdk.props", @"C:\sln\App\obj\App.csproj.nuget.g.props"];

    private static bool Exists(string path) => string.Equals(path, List, StringComparison.OrdinalIgnoreCase);

    [Fact, Trait("Category", "Unit")]
    public void A_list_written_after_the_evaluation_is_missed()
    {
        GeneratedListImport.Missed(List, Imports, Exists).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_list_the_evaluation_imported_is_watched()
    {
        GeneratedListImport.Missed(List, Imports.Append(List.ToUpperInvariant().Replace('\\', '/')), Exists).ShouldBeFalse();
    }

    // Before the first build of all, or a project whose bindings were never generated.
    [Fact, Trait("Category", "Unit")]
    public void No_list_on_disk_is_nothing_missed()
    {
        GeneratedListImport.Missed(List, Imports, _ => false).ShouldBeFalse();
    }

    // Visual Studio's evaluations leave out private properties such as the package's own _DamlGeneratedProps,
    // so the path is worked out from public ones, the way the package does.
    [Fact, Trait("Category", "Unit")]
    public void The_list_path_comes_from_the_projects_public_properties()
    {
        GeneratedListImport.ListPath(@"C:\sln\App", @"obj\", "App.csproj", "Debug", "net10.0").ShouldBe(List);
        GeneratedListImport.ListPath(@"C:\sln\App", @"D:\out\App\", "App.csproj", "Release", "net8.0").ShouldBe(@"D:\out\App\daml\App.csproj.Release.net8.0.g.props");
    }

    // The outer evaluation of a project with several target frameworks has none.
    [Theory, Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_target_framework_there_is_no_list_path(string? framework)
    {
        GeneratedListImport.ListPath(@"C:\sln\App", @"obj\", "App.csproj", "Debug", framework).ShouldBeNull();
    }

    // Evaluated again once; if that doesn't help (Visual Studio may still hold what it found missing),
    // a reload is asked for once, and nothing more until the list is seen.
    [Fact, Trait("Category", "Unit")]
    public void A_missed_list_is_evaluated_again_once_then_a_reload_is_asked_for_once()
    {
        var lists = new MissedLists();

        var actions = new[]
        {
            lists.Next(List, missed: true),
            lists.Next(List, missed: true),
            lists.Next(List, missed: true),
        };

        actions.ShouldBe([MissedListAction.EvaluateAgain, MissedListAction.AskForReload, MissedListAction.None]);
    }

    // Once seen, the list is forgotten: a later Clean of an older package, or a deleted obj, starts afresh.
    [Fact, Trait("Category", "Unit")]
    public void A_list_seen_again_starts_afresh()
    {
        var lists = new MissedLists();
        lists.Next(List, missed: true);
        lists.Next(List, missed: true);

        lists.Next(List.ToUpperInvariant(), missed: false).ShouldBe(MissedListAction.None);
        lists.Next(List, missed: true).ShouldBe(MissedListAction.EvaluateAgain);
    }

    // A project without the package, or the outer evaluation of one with several target frameworks.
    [Theory, Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_project_that_names_no_list_misses_none(string? list)
    {
        GeneratedListImport.Missed(list, Imports, _ => true).ShouldBeFalse();
    }
}
