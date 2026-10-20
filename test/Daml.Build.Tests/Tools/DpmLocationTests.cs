using System.Collections.Generic;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.Tools;
using Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Tools;

public class DpmLocationTests
{
    private const string Project = @"C:\work\Contracts";
    private const string AppData = @"C:\Users\ann\AppData\Roaming";

    private readonly InMemoryFileSystem _files = new();
    private readonly Dictionary<string, string> _environment = new() { ["APPDATA"] = AppData, ["PATH"] = @"C:\Windows;C:\tools" };
    private readonly List<ConfigurationError> _errors = [];

    private string? Find(string? configured) =>
        DpmLocation.Find(configured, Project, name => _environment.TryGetValue(name, out var value) ? value : null, _files, _errors);

    [Fact, Trait("Category", "Unit")]
    public void A_manifest_outside_the_project_uses_the_child_directory_for_its_relative_home()
    {
        _environment["DPM_HOME"] = "tools";
        _files.AddFile(@"D:\package\tools\bin\dpm.exe", "");
        _files.AddFile(Project + @"\explicit\dpm.exe", "");
        string? Locate(string? configured) => DpmLocation.Find(configured, Project,
            name => _environment.TryGetValue(name, out var value) ? value : null, _files, _errors, @"D:\package");
        Locate(null).ShouldBe(@"D:\package\tools\bin\dpm.exe");
        Locate(@"explicit\dpm.exe").ShouldBe(Project + @"\explicit\dpm.exe");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData(@"..\tools")]
    [InlineData(@"D:\configuration-only")]
    public void A_home_without_an_executable_does_not_hide_the_installed_command(string home)
    {
        _environment["DPM_HOME"] = home;
        _files.AddFile(AppData + @"\dpm\bin\dpm.exe", "");
        Find(null).ShouldBe(AppData + @"\dpm\bin\dpm.exe");
        _errors.ShouldBeEmpty();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("", @"C:\work\Contracts")]
    [InlineData(@"..\tools", @"C:\work\tools")]
    public void Relative_home_commands_are_found_against_the_child_working_directory(string home, string resolved)
    {
        _environment["DPM_HOME"] = home;
        _files.AddFile(resolved + @"\bin\dpm.exe", "");
        Find(null).ShouldBe(resolved + @"\bin\dpm.exe");
    }

    // The extension's own search, so a build runs the dpm the editor does.
    [Fact, Trait("Category", "Unit")]
    public void Unset_it_is_searched_on_PATH_first_and_then_in_its_install_folder()
    {
        _files.AddFile(AppData + @"\dpm\bin\dpm.cmd", "");
        Find(null).ShouldBe(AppData + @"\dpm\bin\dpm.cmd");

        _files.AddFile(@"C:\tools\dpm.exe", "");
        Find("").ShouldBe(@"C:\tools\dpm.exe");
        _errors.ShouldBeEmpty();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(null, @"C:\Users\ann\AppData\Roaming\.dpm")]
    [InlineData(@"D:\Dpm Home", @"D:\Dpm Home")]
    public void Default_search_includes_the_documented_and_configured_DPM_home(string? configuredHome, string home)
    {
        if (configuredHome is not null)
        {
            _environment["DPM_HOME"] = configuredHome;
        }
        _files.AddFile(home + @"\bin\dpm.exe", "");

        Find(null).ShouldBe(home + @"\bin\dpm.exe");
        _errors.ShouldBeEmpty();

        _files.AddFile(@"C:\tools\dpm.exe", "");
        Find(null).ShouldBe(@"C:\tools\dpm.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_name_is_looked_up_on_PATH_only()
    {
        _files.AddFile(@"C:\tools\dpm-next.cmd", "").AddFile(AppData + @"\dpm\bin\dpm-next.cmd", "");

        Find("dpm-next").ShouldBe(@"C:\tools\dpm-next.cmd");
        Find("dpm-next.cmd").ShouldBe(@"C:\tools\dpm-next.cmd");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\dpm\bin\dpm.cmd", @"C:\dpm\bin\dpm.cmd")]
    [InlineData(@"..\tools\dpm.exe", @"C:\work\tools\dpm.exe")]
    [InlineData("\"C:\\dpm\\bin\\dpm.cmd\"", @"C:\dpm\bin\dpm.cmd")]
    public void A_path_is_taken_as_written_against_the_project(string configured, string expected)
    {
        _files.AddFile(expected, "");

        Find(configured).ShouldBe(expected);
    }

    // A typo must not quietly run some other dpm.
    [Fact, Trait("Category", "Unit")]
    public void A_set_path_that_isnt_there_is_an_error_not_a_fallback()
    {
        _files.AddFile(@"C:\tools\dpm.exe", "");

        Find(@"C:\dmp\bin\dpm.cmd").ShouldBeNull();
        var error = _errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.MissingTool);
        error.Message.ShouldBe(@"DamlDpmPath is 'C:\dmp\bin\dpm.cmd', but there is no file at C:\dmp\bin\dpm.cmd.");
    }

    [Fact, Trait("Category", "Unit")]
    public void No_dpm_anywhere_says_where_it_was_looked_for()
    {
        Find(null).ShouldBeNull();

        var error = _errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.MissingTool);
        error.Message.ShouldContain("DPM_HOME");
        error.Message.ShouldContain(DamlProperties.DpmPath.Name);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_name_not_on_PATH_is_an_error_naming_it()
    {
        Find("dpm-next").ShouldBeNull();

        _errors.ShouldHaveSingleItem().Message.ShouldBe("DamlDpmPath is 'dpm-next', but no dpm-next is on PATH.");
    }
}
