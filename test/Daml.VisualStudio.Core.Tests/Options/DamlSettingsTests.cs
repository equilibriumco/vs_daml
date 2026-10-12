using Daml.VisualStudio.Core.Options;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Server;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Options;

public class DamlSettingsTests
{
    [Fact, Trait("Category", "Unit")]
    public void Defaults_run_multi_ide_at_info_with_keep_alive_and_no_autorun()
    {
        var settings = new DamlSettings();

        settings.DpmPath.ShouldBeNull();
        settings.DamlAssistantPath.ShouldBeNull();
        settings.UseMultiIde.ShouldBeTrue();
        settings.LogLevel.ShouldBe(ServerLogLevel.Info);
        settings.ExtraArguments.ShouldBeNull();
        settings.AutorunAllTests.ShouldBeFalse();
        settings.KeepAliveEnabled.ShouldBeTrue();
        settings.ServerMode.ShouldBe(ServerMode.MultiIde);
    }

    [Fact, Trait("Category", "Unit")]
    public void Defaults_map_to_default_server_options()
    {
        new DamlSettings().ToLanguageServerOptions().ShouldBe(new LanguageServerOptions());
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_server_setting_is_carried_over_trimmed()
    {
        var settings = new DamlSettings
        {
            DpmPath = @"  C:\tools\dpm.exe ",
            DamlAssistantPath = "\tC:\\daml\\bin\\daml.cmd\t",
            LogLevel = ServerLogLevel.Debug,
            ExtraArguments = " --foo \"a b\" ",
            AutorunAllTests = true,
        };

        settings.ToLanguageServerOptions().ShouldBe(new LanguageServerOptions
        {
            DpmPath = @"C:\tools\dpm.exe",
            DamlAssistantPath = @"C:\daml\bin\daml.cmd",
            LogLevel = ServerLogLevel.Debug,
            ExtraArguments = "--foo \"a b\"",
            AutorunAllTests = true,
        });
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_and_arguments_mean_not_set(string blank)
    {
        var options = new DamlSettings { DpmPath = blank, DamlAssistantPath = blank, ExtraArguments = blank }.ToLanguageServerOptions();

        options.DpmPath.ShouldBeNull();
        options.DamlAssistantPath.ShouldBeNull();
        options.ExtraArguments.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Single_package_mode_when_multi_ide_is_off()
    {
        new DamlSettings { UseMultiIde = false }.ServerMode.ShouldBe(ServerMode.Ide);
    }

    [Fact, Trait("Category", "Unit")]
    public void Nothing_changed_needs_no_restart()
    {
        DamlSettings.ChangesNeedingRestart(new DamlSettings(), new DamlSettings()).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_setting_that_shapes_the_server_command_needs_a_restart()
    {
        var before = new DamlSettings();
        var after = new DamlSettings
        {
            DpmPath = @"C:\tools\dpm.exe",
            DamlAssistantPath = @"C:\daml\daml.cmd",
            UseMultiIde = false,
            LogLevel = ServerLogLevel.Debug,
            ExtraArguments = "--foo",
            AutorunAllTests = true,
        };

        DamlSettings.ChangesNeedingRestart(before, after).ShouldBe(
            ["DpmPath", "DamlAssistantPath", "UseMultiIde", "LogLevel", "ExtraArguments", "AutorunAllTests"]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Keep_alive_applies_without_a_restart()
    {
        DamlSettings.ChangesNeedingRestart(new DamlSettings(), new DamlSettings { KeepAliveEnabled = false }).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Edits_that_start_the_server_the_same_way_need_no_restart()
    {
        var before = new DamlSettings { DpmPath = @"C:\Tools\dpm.exe", ExtraArguments = "--foo" };
        var after = new DamlSettings { DpmPath = @" c:\tools\DPM.EXE ", ExtraArguments = "--foo  " };

        DamlSettings.ChangesNeedingRestart(before, after).ShouldBeEmpty();
        DamlSettings.ChangesNeedingRestart(new DamlSettings { ExtraArguments = "" }, new DamlSettings()).ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void Argument_case_matters()
    {
        DamlSettings.ChangesNeedingRestart(new DamlSettings { ExtraArguments = "--Foo" }, new DamlSettings { ExtraArguments = "--foo" })
            .ShouldBe(["ExtraArguments"]);
    }
}
