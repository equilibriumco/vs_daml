using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Tasks;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tasks;

public class DamlCleanGeneratedTests
{
    private const string Project = @"C:\work\App";

    private readonly FakeBuildEngine _engine = new();
    private readonly FakeOrchestrator _orchestrator = new();

    private DamlCleanGenerated Task() => new(_orchestrator)
    {
        BuildEngine = _engine,
        ProjectPath = Project + @"\App.csproj",
        IntermediateOutputPath = @"obj\Debug\net10.0\",
        Configuration = "Debug",
        TargetFramework = "net10.0",
    };

    [Theory, Trait("Category", "Unit")]
    [InlineData(null, Project + @"\obj\Debug\net10.0\Daml")]
    [InlineData("Source", Project + @"\Generated\Daml")]
    public void Clean_asks_the_orchestrator_for_the_projects_generation_root(string? mode, string root)
    {
        var task = Task();
        task.GeneratedOutputMode = mode;

        task.Execute().ShouldBeTrue();

        var request = _orchestrator.Cleans.ShouldHaveSingleItem();
        (request.ProjectPath, request.Codegen.GeneratedRoot, request.Inputs.Count).ShouldBe((Project + @"\App.csproj", root, 0));
        _orchestrator.Requests.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void What_the_clean_says_is_logged()
    {
        _orchestrator.Result = new CodegenResult(
            [],
            [
                new BuildMessage(DamlDiagnosticSeverity.Information, null, null, Project + @"\App.csproj", 0, 0, 0, 0, "Removed the generated C# in it."),
                new BuildMessage(DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.LockTimeout, null, Project + @"\App.csproj", 0, 0, 0, 0, "Another build kept it."),
            ]);

        Task().Execute().ShouldBeFalse();

        _engine.Messages.ShouldContain(m => m.Message == "Removed the generated C# in it.");
        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.LockTimeout);
    }

    [Fact, Trait("Category", "Unit")]
    public void Settings_that_cant_be_used_stop_the_clean_before_it_starts()
    {
        var task = Task();
        task.GeneratedOutputMode = "Both";

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        _orchestrator.Cleans.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_cancelled_clean_is_EQD0110()
    {
        var task = Task();
        _orchestrator.OnRun = () => task.Cancel();

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.CancelledOrTimedOut);
    }
}
