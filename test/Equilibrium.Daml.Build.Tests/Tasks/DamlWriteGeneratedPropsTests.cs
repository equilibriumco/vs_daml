using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Equilibrium.Daml.Build.Tasks;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tasks;

public sealed class DamlWriteGeneratedPropsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-props-task-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBuildEngine _engine = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Props => Path.Combine(_root, "App.csproj.Debug.net10.0.g.props");

    private DamlWriteGeneratedProps Task(params string[] files) => new()
    {
        BuildEngine = _engine,
        PropsPath = Props,
        Files = files.Select(f =>
        {
            var item = new TaskItem(Path.Combine(_root, f));
            item.SetMetadata("EqDamlGenerationId", "0123456789abcdef");
            return item;
        }).ToArray(),
    };

    private string[] Listed() =>
        XDocument.Load(Props).Root!.Descendants("Compile").Select(c => (string)c.Attribute("Include")!).ToArray();

    [Fact, Trait("Category", "Unit")]
    public void A_real_build_lists_its_generated_files()
    {
        Task(@"Daml\Asset.cs", @"Daml\Asset.Give.cs").Execute().ShouldBeTrue();

        Listed().ShouldBe([Path.Combine(_root, @"Daml\Asset.cs"), Path.Combine(_root, @"Daml\Asset.Give.cs")]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_design_time_build_makes_an_empty_list_where_there_is_none_and_keeps_one_that_is_there()
    {
        var task = Task();
        task.OnlyIfMissing = true;

        task.Execute().ShouldBeTrue();
        Listed().ShouldBeEmpty();

        Task(@"Daml\Asset.cs").Execute().ShouldBeTrue();
        task.Execute().ShouldBeTrue();
        Listed().ShouldHaveSingleItem();
    }

    // The build compiles the right files either way; only IntelliSense would be a build behind.
    [Fact, Trait("Category", "Unit")]
    public void A_list_that_cant_be_written_is_a_warning()
    {
        Directory.CreateDirectory(Props);

        Task(@"Daml\Asset.cs").Execute().ShouldBeTrue();

        _engine.Errors.ShouldBeEmpty();
        _engine.Warnings.ShouldHaveSingleItem().Message!.ShouldContain("only after the next build");
    }
}
