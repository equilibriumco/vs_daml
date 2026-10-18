using Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

// The MSBuild run behind a background refresh, and what is read back from what it prints.
public class BindingRefreshCommandTests
{
    // The configuration Visual Studio builds in, the references' DARs but no compiled C# library, and no
    // summary repeating each error. The project's own setting decides whether tools may be installed.
    [Fact, Trait("Category", "Unit")]
    public void A_refresh_runs_the_targeted_build_in_the_active_configuration()
    {
        var arguments = BindingRefreshCommand.Arguments(new BindingRefreshTarget(@"C:\sln\App\App.csproj", null), "Debug", "AnyCPU", @"C:\sln");

        arguments.ShouldBe([
            @"C:\sln\App\App.csproj", "-t:DamlRefreshBindings", "-p:Configuration=Debug", "-p:Platform=AnyCPU", "-p:BuildProjectReferences=false",
            @"-p:SolutionDir=C:\sln\", "-nologo", "-nodeReuse:false", "-verbosity:minimal", "-consoleLoggerParameters:NoSummary"]);
        arguments.ShouldNotContain(a => a.Contains("DamlAllowToolInstall"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_project_that_builds_for_several_frameworks_is_refreshed_in_one()
    {
        BindingRefreshCommand.Arguments(new BindingRefreshTarget(@"C:\sln\Web\Web.csproj", "net10.0"), "Release", "x64", null)
            .ShouldContain("-p:TargetFramework=net10.0");
    }

    // Visual Studio names the platform of a solution configuration "Any CPU"; MSBuild wants "AnyCPU".
    [Theory, Trait("Category", "Unit")]
    [InlineData("Debug|Any CPU", "Debug", "AnyCPU")]
    [InlineData("Release|x64", "Release", "x64")]
    [InlineData("Debug", "Debug", "AnyCPU")]
    public void The_project_configuration_is_split_as_MSBuild_wants_it(string canonical, string configuration, string platform)
    {
        BindingRefreshCommand.Configuration(canonical).ShouldBe((configuration, platform));
    }

    [Fact, Trait("Category", "Unit")]
    public void MSBuild_of_the_running_Visual_Studio_is_its_64_bit_one()
    {
        BindingRefreshCommand.MSBuildOf(@"C:\VS\18\Community\Common7\IDE\").ShouldBe(@"C:\VS\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void Errors_and_warnings_are_read_from_what_MSBuild_printed()
    {
        const string Output = """
              Contracts -> C:\sln\Contracts\.daml\dist\contracts-0.1.0.dar
            C:\sln\Contracts\daml\Asset.daml(5,9,5,20): error DAML0001: Couldn't match expected type 'Int' with actual type 'Text' [C:\sln\Contracts\Contracts.msbuildproj]
            C:\sln\App\App.csproj : error DAML0104: dpm codegen-cs failed for ..\Contracts\Contracts.msbuildproj (exit code 1): [C:\sln\App\App.csproj]
            C:\sln\App\App.csproj : warning DAML0111: a generated file's path is 270 characters long [C:\sln\App\App.csproj]
            C:\sln\Contracts\daml\Asset.daml(5,9,5,20): error DAML0001: Couldn't match expected type 'Int' with actual type 'Text' [C:\sln\Contracts\Contracts.msbuildproj]
            """;

        var diagnostics = MSBuildOutput.Diagnostics(Output);

        diagnostics.ShouldBe([
            new MSBuildDiagnostic(@"C:\sln\Contracts\daml\Asset.daml", 5, 9, true, "DAML0001", "Couldn't match expected type 'Int' with actual type 'Text'"),
            new MSBuildDiagnostic(@"C:\sln\App\App.csproj", 0, 0, true, "DAML0104", @"dpm codegen-cs failed for ..\Contracts\Contracts.msbuildproj (exit code 1):"),
            new MSBuildDiagnostic(@"C:\sln\App\App.csproj", 0, 0, false, "DAML0111", "a generated file's path is 270 characters long"),
        ]);
    }
}
