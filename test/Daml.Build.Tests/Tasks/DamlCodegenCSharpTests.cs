using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Daml.Build.Core.Building;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Tasks;

public class DamlCodegenCSharpTests
{
    private const string Project = @"C:\work\App";

    private readonly FakeBuildEngine _engine = new();
    private readonly FakeOrchestrator _orchestrator = new();

    private DamlCodegenCSharp Task(params ITaskItem[] references) => new(_orchestrator)
    {
        BuildEngine = _engine,
        ProjectPath = Project + @"\App.csproj",
        IntermediateOutputPath = @"obj\Debug\net10.0\",
        Configuration = "Debug",
        TargetFramework = "net10.0",
        References = references,
    };

    private static TaskItem Reference(string source, string include, string? @namespace = null, string? dar = null)
    {
        var item = new TaskItem(include);
        item.SetMetadata("DamlReferenceSource", source);
        if (@namespace is not null)
        {
            item.SetMetadata("Namespace", @namespace);
        }
        if (dar is not null)
        {
            item.SetMetadata("DarPath", dar);
        }
        return item;
    }

    private static TaskItem Contracts(string? @namespace = "Contracts") =>
        Reference("DamlReference", @"..\Contracts\Contracts.msbuildproj", @namespace, @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar");

    [Fact, Trait("Category", "Unit")]
    public void The_orchestrator_gets_the_project_its_settings_and_one_input_per_generation()
    {
        var task = Task(Contracts(), Reference("DamlDar", @"..\vendor\finance-1.0.0.dar", "Finance"));
        task.Offline = "true";
        task.RuntimeVersion = "0.6.0";

        task.Execute().ShouldBeTrue();

        var request = _orchestrator.Requests.ShouldHaveSingleItem();
        (request.ProjectPath, request.Configuration, request.TargetFramework, request.DesignTime).ShouldBe((Project + @"\App.csproj", "Debug", "net10.0", false));
        request.Tools.Offline.ShouldBeTrue();
        request.RuntimeVersion.ShouldBe("0.6.0");
        request.Codegen.GeneratedRoot.ShouldBe(Project + @"\obj\Debug\net10.0\Daml");
        request.Inputs.Select(i => (i.Reference.Kind, i.Reference.Path, i.Reference.Namespace, i.DarPath)).ShouldBe(
        [
            (DamlReferenceKind.Project, @"C:\work\Contracts\Contracts.msbuildproj", "Contracts", @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar"),
            (DamlReferenceKind.Dar, @"C:\work\vendor\finance-1.0.0.dar", "Finance", @"C:\work\vendor\finance-1.0.0.dar"),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_multi_targeted_project_generating_into_its_sources_stops_before_anything_runs()
    {
        var task = Task(Contracts());
        task.GeneratedOutputMode = "Source";
        task.TargetFrameworks = "net10.0;net11.0";

        task.Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.OutputConflict);
        _orchestrator.Requests.ShouldBeEmpty();
    }

    // What the Daml project's build says about the sources its DAR came from goes into the generation's manifest.
    [Fact, Trait("Category", "Unit")]
    public void A_project_reference_brings_the_sources_its_DAR_was_built_from()
    {
        var contracts = Contracts();
        contracts.SetMetadata("DamlManifest", @"..\Contracts\daml.yaml");
        contracts.SetMetadata("DamlSourceFingerprint", "50c");

        Task(contracts, Reference("DamlDar", @"..\vendor\finance-1.0.0.dar", "Finance"), Reference("DamlReference", @"..\Other\Other.msbuildproj", null, @"C:\work\Other\other.dar"))
            .Execute().ShouldBeTrue();

        _orchestrator.Requests.ShouldHaveSingleItem().Inputs.Select(i => i.Source).ShouldBe(
            [new SourceProvenance(@"C:\work\Contracts\daml.yaml", "50c"), null, null]);
    }

    private static TaskItem Answer(string project, string dar, string? manifest = null, string? fingerprint = null)
    {
        var item = new TaskItem(dar);
        item.SetMetadata("MSBuildSourceProjectFile", project);
        if (manifest is not null)
        {
            item.SetMetadata("DamlManifest", manifest);
            item.SetMetadata("DamlSourceFingerprint", fingerprint);
        }
        return item;
    }

    // A ProjectReference is a Daml one only when its project answered GetDamlDar; the rest are plain C# libraries.
    [Fact, Trait("Category", "Unit")]
    public void A_project_reference_is_generated_from_what_its_project_answered_and_the_others_are_left_alone()
    {
        var task = Task(
            Reference("ProjectReference", @"..\Contracts\Contracts.msbuildproj", "Contracts"),
            Reference("ProjectReference", @"..\Library\Library.csproj"));
        task.Dars = [Answer(@"C:\work\Contracts\Contracts.msbuildproj", @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar", @"C:\work\Contracts\daml.yaml", "50c")];

        task.Execute().ShouldBeTrue();

        _engine.Errors.ShouldBeEmpty();
        var input = _orchestrator.Requests.ShouldHaveSingleItem().Inputs.ShouldHaveSingleItem();
        (input.Reference.Kind, input.Reference.Path, input.Reference.Namespace, input.DarPath).ShouldBe(
            (DamlReferenceKind.Project, @"C:\work\Contracts\Contracts.msbuildproj", "Contracts", @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar"));
        input.Source.ShouldBe(new SourceProvenance(@"C:\work\Contracts\daml.yaml", "50c"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_Daml_reference_takes_its_DAR_from_its_projects_answer()
    {
        var task = Task(Reference("DamlReference", @"..\Contracts\Contracts.msbuildproj", "Contracts"));
        task.Dars = [Answer(@"C:\work\Contracts\Contracts.msbuildproj", @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar")];

        task.Execute().ShouldBeTrue();

        var input = _orchestrator.Requests.ShouldHaveSingleItem().Inputs.ShouldHaveSingleItem();
        (input.DarPath, input.Source).ShouldBe((@"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar", (SourceProvenance?)null));
    }

    // Only C# libraries referenced: nothing to generate, and the generations of earlier references go.
    [Fact, Trait("Category", "Unit")]
    public void Without_any_Daml_reference_the_orchestrator_gets_no_inputs()
    {
        Task(Reference("ProjectReference", @"..\Library\Library.csproj")).Execute().ShouldBeTrue();

        _orchestrator.Requests.ShouldHaveSingleItem().Inputs.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Generated_files_come_back_as_compile_items_owned_by_their_generation()
    {
        _orchestrator.Result = new CodegenResult(
            [new GeneratedFile(@"C:\work\App\obj\Debug\net10.0\Daml\Contracts-1\Asset.cs", "1"), new GeneratedFile(@"C:\work\App\obj\Debug\net10.0\Daml\Contracts-1\Asset.Give.cs", "1")],
            []);
        var task = Task(Contracts());

        task.Execute().ShouldBeTrue();

        task.GeneratedFiles.Select(f => (f.ItemSpec, f.GetMetadata("DamlGenerated"), f.GetMetadata("DamlGenerationId"))).ShouldBe(
        [
            (@"C:\work\App\obj\Debug\net10.0\Daml\Contracts-1\Asset.cs", "true", "1"),
            (@"C:\work\App\obj\Debug\net10.0\Daml\Contracts-1\Asset.Give.cs", "true", "1"),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_orchestrators_errors_fail_the_task_at_their_place()
    {
        _orchestrator.Result = new CodegenResult([], [new BuildMessage(DamlDiagnosticSeverity.Error, DamlDiagnosticCodes.CodegenFailed, null, @"C:\work\vendor\finance-1.0.0.dar", 0, 0, 0, 0, "it broke {0}")]);

        Task(Contracts()).Execute().ShouldBeFalse();

        var error = _engine.Errors.ShouldHaveSingleItem();
        (error.Code, error.File, error.Message).ShouldBe((DamlDiagnosticCodes.CodegenFailed, @"C:\work\vendor\finance-1.0.0.dar", "it broke {0}"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_design_time_build_says_so()
    {
        var task = Task(Contracts());
        task.DesignTime = true;

        task.Execute().ShouldBeTrue();

        _orchestrator.Requests.ShouldHaveSingleItem().DesignTime.ShouldBeTrue();
    }

    // Each bad setting and reference is reported in the one build, and nothing is generated from them.
    [Fact, Trait("Category", "Unit")]
    public void Bad_settings_and_references_are_all_reported_and_nothing_runs()
    {
        var task = Task(Contracts(), Contracts(), Reference("DamlReference", @"..\vendor\x.dar"));
        task.CodegenComponent = "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:latest";
        task.LockTimeoutSeconds = "0";

        task.Execute().ShouldBeFalse();

        _engine.Errors.Select(e => e.Code).ShouldBe(
            [DamlDiagnosticCodes.InvalidConfiguration, DamlDiagnosticCodes.GeneratorPinConflict, DamlDiagnosticCodes.InvalidConfiguration, DamlDiagnosticCodes.OutputConflict],
            ignoreOrder: true);
        _engine.Errors.ShouldAllBe(e => e.File == Project + @"\App.csproj");
        _orchestrator.Requests.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_project_reference_without_its_DAR_is_a_codegen_error()
    {
        Task(Reference("DamlReference", @"..\Contracts\Contracts.msbuildproj")).Execute().ShouldBeFalse();

        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        _orchestrator.Requests.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void With_codegen_off_nothing_runs_and_nothing_is_compiled()
    {
        var task = Task(Contracts());
        task.CodegenEnabled = "false";

        task.Execute().ShouldBeTrue();

        _orchestrator.Requests.ShouldBeEmpty();
        task.GeneratedFiles.ShouldBeEmpty();
    }

    // A file another process holds, a DAR being written for one, is an error of the build like any
    // other, never a task that fell over.
    [Fact, Trait("Category", "Unit")]
    public void A_file_the_generation_couldnt_read_or_write_is_DAML0104_naming_it()
    {
        var task = Task(Contracts());
        _orchestrator.OnRun = () => throw new IOException(@"The process cannot access the file 'C:\x\contracts.dar' because it is being used by another process.");

        task.Execute().ShouldBeFalse();

        var error = _engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.CodegenFailed);
        error.Message!.ShouldContain(@"C:\x\contracts.dar");
        error.Message!.ShouldContain("build again");
        task.GeneratedFiles.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Cancelling_the_task_cancels_the_orchestrators_work_and_fails_with_DAML0110()
    {
        var task = Task(Contracts());
        _orchestrator.OnRun = () => task.Cancel();

        task.Execute().ShouldBeFalse();

        _orchestrator.Tokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeTrue();
        _engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(DamlDiagnosticCodes.CancelledOrTimedOut);
        task.GeneratedFiles.ShouldBeEmpty();
    }
}
