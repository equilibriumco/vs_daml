using System.Collections.Generic;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Sdk;

public class AssistantLocatorTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly Dictionary<string, string> _environment = new()
    {
        ["APPDATA"] = @"C:\Users\ann\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\ann",
        ["PATH"] = @"C:\Windows\system32;C:\tools",
    };

    private AssistantLocator Locator() => new(_files, name => _environment.TryGetValue(name, out var value) ? value : null);

    [Fact, Trait("Category", "Unit")]
    public void Dpm_on_PATH_is_found_with_the_exe_preferred_over_the_cmd_wrapper()
    {
        _files.AddFile(@"C:\tools\dpm.cmd", "");
        _files.AddFile(@"C:\tools\dpm.exe", "");

        Locator().Find(AssistantKind.Dpm, null).ShouldBe(new AssistantLocation(AssistantKind.Dpm, @"C:\tools\dpm.exe"));
    }

    [Fact, Trait("Category", "Unit")]
    public void The_first_PATH_entry_with_the_tool_wins()
    {
        _environment["PATH"] = @"""C:\first dir"";;C:\tools";
        _files.AddFile(@"C:\tools\dpm.exe", "");
        _files.AddFile(@"C:\first dir\dpm.cmd", "");

        Locator().Find(AssistantKind.Dpm, null)!.Path.ShouldBe(@"C:\first dir\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void Dpm_falls_back_to_its_install_folder_under_APPDATA()
    {
        _files.AddFile(@"C:\Users\ann\AppData\Roaming\dpm\bin\dpm.cmd", "");

        Locator().Find(AssistantKind.Dpm, null)!.Path.ShouldBe(@"C:\Users\ann\AppData\Roaming\dpm\bin\dpm.cmd");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(null, @"C:\Users\ann\AppData\Roaming\.dpm")]
    [InlineData(@"D:\Dpm Home", @"D:\Dpm Home")]
    public void Dpm_is_found_in_the_documented_or_configured_home(string? configuredHome, string home)
    {
        if (configuredHome is not null)
        {
            _environment["DPM_HOME"] = configuredHome;
        }
        _files.AddFile(home + @"\bin\dpm.exe", "");

        Locator().Find(AssistantKind.Dpm, null).ShouldBe(new AssistantLocation(AssistantKind.Dpm, home + @"\bin\dpm.exe"));
    }

    [Fact, Trait("Category", "Unit")]
    public void The_daml_assistant_falls_back_to_its_install_folder_in_the_profile()
    {
        _files.AddFile(@"C:\Users\ann\.daml\bin\daml.cmd", "");

        Locator().Find(AssistantKind.DamlAssistant, null).ShouldBe(new AssistantLocation(AssistantKind.DamlAssistant, @"C:\Users\ann\.daml\bin\daml.cmd"));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_configured_path_wins_over_PATH()
    {
        _files.AddFile(@"C:\tools\dpm.exe", "");
        _files.AddFile(@"D:\pinned\dpm.exe", "");

        Locator().Find(AssistantKind.Dpm, @"D:\pinned\dpm.exe")!.Path.ShouldBe(@"D:\pinned\dpm.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_configured_path_that_does_not_exist_is_an_error_not_a_silent_fallback()
    {
        _files.AddFile(@"C:\tools\dpm.exe", "");

        var error = Should.Throw<SdkResolutionException>(() => Locator().Find(AssistantKind.Dpm, @"D:\gone\dpm.exe"));

        error.Code.ShouldBe("EQD0101");
        error.Message.ShouldContain(@"D:\gone\dpm.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void Nothing_installed_finds_nothing()
    {
        _environment.Remove("PATH");

        Locator().Find(AssistantKind.Dpm, null).ShouldBeNull();
        Locator().Find(AssistantKind.DamlAssistant, "").ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_configured_path_pasted_with_quotes_is_accepted()
    {
        _files.AddFile(@"C:\tools\dpm.cmd", "");

        Locator().Find(AssistantKind.Dpm, "\"C:\\tools\\dpm.cmd\" ")!.Path.ShouldBe(@"C:\tools\dpm.cmd");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("dpm.cmd")]
    [InlineData(@"C:\tools\dpm|x.cmd")]
    public void A_configured_path_that_isnt_a_full_path_is_an_explained_failure_not_a_crash(string configured)
    {
        var ex = Should.Throw<SdkResolutionException>(() => Locator().Find(AssistantKind.Dpm, configured));

        ex.Message.ShouldContain("Tools > Options > EQ Daml Tools");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_PATH_entry_that_isnt_a_path_is_skipped()
    {
        _environment["PATH"] = @"C:\bro<ken;C:\tools";
        _files.AddFile(@"C:\tools\dpm.exe", "");

        Locator().Find(AssistantKind.Dpm, null)!.Path.ShouldBe(@"C:\tools\dpm.exe");
    }
}
