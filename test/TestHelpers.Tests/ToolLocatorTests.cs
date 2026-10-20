using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;
using Xunit;

namespace Daml.TestHelpers.Tests;

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
        const string bare = @"C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe";
        const string usable = @"C:\VisualStudio\MSBuild\Current\Bin\MSBuild.exe";
        _files.UnionWith([bare, usable, @"C:\VisualStudio\MSBuild\Current\Bin\SdkResolvers\Microsoft.DotNet.MSBuildSdkResolver\Microsoft.DotNet.MSBuildSdkResolver.dll"]);

        ToolLocator.SelectVisualStudioMSBuild([bare, usable], _files.Contains).ShouldBe(usable);
        ToolLocator.SelectVisualStudioMSBuild([bare], _files.Contains).ShouldBeNull();
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
