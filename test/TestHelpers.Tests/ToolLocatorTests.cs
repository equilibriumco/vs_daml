using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.TestHelpers.Tests;

public class ToolLocatorTests
{
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    private string? Find(ExternalTool tool) => ToolLocator.Find(tool, name => _env.TryGetValue(name, out var v) ? v : null, _files.Contains);

    [Fact, Trait("Category", "Unit")]
    public void Finds_tool_on_PATH_first()
    {
        _env["PATH"] = string.Join(Path.PathSeparator.ToString(), @"C:\a", @"C:\tools");
        _env["APPDATA"] = @"C:\Users\x\AppData\Roaming";
        _files.Add(@"C:\tools\dpm.exe");
        _files.Add(@"C:\Users\x\AppData\Roaming\dpm\bin\dpm.cmd");

        Find(ExternalTool.Dpm).ShouldBe(@"C:\tools\dpm.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void Falls_back_to_dpm_home_under_AppData()
    {
        _env["APPDATA"] = @"C:\Users\x\AppData\Roaming";
        _files.Add(@"C:\Users\x\AppData\Roaming\dpm\bin\dpm.cmd");

        Find(ExternalTool.Dpm).ShouldBe(@"C:\Users\x\AppData\Roaming\dpm\bin\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void DPM_HOME_overrides_the_default_location()
    {
        _env["APPDATA"] = @"C:\Users\x\AppData\Roaming";
        _env["DPM_HOME"] = @"D:\dpm";
        _files.Add(@"C:\Users\x\AppData\Roaming\dpm\bin\dpm.cmd");
        _files.Add(@"D:\dpm\bin\dpm.cmd");

        Find(ExternalTool.Dpm).ShouldBe(@"D:\dpm\bin\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void Finds_java_through_JAVA_HOME()
    {
        _env["JAVA_HOME"] = @"C:\jdk-21";
        _files.Add(@"C:\jdk-21\bin\java.exe");

        Find(ExternalTool.Java).ShouldBe(@"C:\jdk-21\bin\java.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void Prefers_JAVA_HOME_over_a_java_on_PATH()
    {
        // The codegen puts JAVA_HOME\bin first, so that is the java a test must check.
        _env["PATH"] = @"C:\Windows\system32;C:\old-jre\bin";
        _env["JAVA_HOME"] = @"C:\jdk-21";
        _files.Add(@"C:\old-jre\bin\java.exe");
        _files.Add(@"C:\jdk-21\bin\java.exe");

        Find(ExternalTool.Java).ShouldBe(@"C:\jdk-21\bin\java.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void MSBuild_discovery_skips_an_installation_without_the_Dotnet_SDK_resolver()
    {
        const string Bare = @"C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe";
        const string Usable = @"C:\VisualStudio\MSBuild\Current\Bin\MSBuild.exe";
        _files.UnionWith([Bare, Usable, @"C:\VisualStudio\MSBuild\Current\Bin\SdkResolvers\Microsoft.DotNet.MSBuildSdkResolver\Microsoft.DotNet.MSBuildSdkResolver.dll"]);

        ToolLocator.SelectVisualStudioMSBuild([Bare, Usable], _files.Contains).ShouldBe(Usable);
        ToolLocator.SelectVisualStudioMSBuild([Bare], _files.Contains).ShouldBeNull();
    }

    // The MSBuild tests also run Visual Studio's design-time builds, whose targets only an IDE installs;
    // Build Tools with the .NET workload builds everything else, so it is only the fallback.
    [Fact, Trait("Category", "Unit")]
    public void MSBuild_discovery_prefers_an_installation_with_the_design_time_targets()
    {
        const string BuildTools = @"C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe";
        const string Ide = @"C:\VisualStudio\MSBuild\Current\Bin\MSBuild.exe";
        _files.UnionWith(
        [
            BuildTools,
            @"C:\BuildTools\MSBuild\Current\Bin\SdkResolvers\Microsoft.DotNet.MSBuildSdkResolver\Microsoft.DotNet.MSBuildSdkResolver.dll",
            Ide,
            @"C:\VisualStudio\MSBuild\Current\Bin\SdkResolvers\Microsoft.DotNet.MSBuildSdkResolver\Microsoft.DotNet.MSBuildSdkResolver.dll",
            @"C:\VisualStudio\MSBuild\Microsoft\VisualStudio\Managed\Microsoft.Managed.DesignTime.targets",
        ]);

        ToolLocator.SelectVisualStudioMSBuild([BuildTools, Ide], _files.Contains).ShouldBe(Ide);
        ToolLocator.SelectVisualStudioMSBuild([BuildTools], _files.Contains).ShouldBe(BuildTools);
    }

    [Fact, Trait("Category", "Unit")]
    public void DPM_default_homes_have_the_same_precedence_as_the_product()
    {
        _env["APPDATA"] = @"C:\AppData";
        _files.UnionWith([@"C:\AppData\.dpm\bin\dpm.cmd", @"C:\AppData\dpm\bin\dpm.cmd"]);

        Find(ExternalTool.Dpm).ShouldBe(@"C:\AppData\.dpm\bin\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void Returns_null_when_nothing_is_installed()
    {
        _env["PATH"] = @"C:\empty";

        Find(ExternalTool.Dpm).ShouldBeNull();
        Find(ExternalTool.Java).ShouldBeNull();
    }
}
