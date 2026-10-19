using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Daml.VisualStudio.Core.Options;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.Options;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

public class OptionsTests
{
    private static PropertyInfo[] PageProperties() =>
        typeof(DamlOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    [Fact, Trait("Category", "Editor")]
    public void A_fresh_page_holds_the_default_settings()
    {
        new DamlOptions().ToSettings().ShouldBe(new DamlSettings { DpmPath = "", DamlAssistantPath = "", ExtraArguments = "" });
        new DamlOptions().ToSettings().ToLanguageServerOptions().ShouldBe(new LanguageServerOptions());
    }

    [Fact, Trait("Category", "Editor")]
    public void Every_setting_on_the_page_reaches_the_snapshot()
    {
        var options = new DamlOptions
        {
            DpmPath = @"C:\dpm.exe",
            DamlAssistantPath = @"C:\daml.cmd",
            UseMultiIde = false,
            LogLevel = ServerLogLevel.Error,
            ExtraArguments = "--x",
            AutorunAllTests = true,
            KeepAliveEnabled = false,
            LiveBindingSync = true,
        };

        options.ToSettings().ShouldBe(new DamlSettings
        {
            DpmPath = @"C:\dpm.exe",
            DamlAssistantPath = @"C:\daml.cmd",
            UseMultiIde = false,
            LogLevel = ServerLogLevel.Error,
            ExtraArguments = "--x",
            AutorunAllTests = true,
            KeepAliveEnabled = false,
            LiveBindingSync = true,
        });
        PageProperties().Length.ShouldBe(8, "a new setting has to be added to ToSettings too");
    }

    // The property grid shows a value in bold when it differs from [DefaultValue], and the
    // settings store only writes non-default values, so the attribute must match the initializer.
    [Fact, Trait("Category", "Editor")]
    public void Every_setting_is_described_and_its_default_attribute_matches_its_initial_value()
    {
        var fresh = new DamlOptions();
        foreach (var property in PageProperties())
        {
            property.GetCustomAttribute<CategoryAttribute>().ShouldNotBeNull(property.Name);
            property.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName.ShouldNotBeNullOrWhiteSpace(property.Name);
            property.GetCustomAttribute<DescriptionAttribute>()!.Description.ShouldNotBeNullOrWhiteSpace(property.Name);
            property.GetCustomAttribute<DefaultValueAttribute>()!.Value.ShouldBe(property.GetValue(fresh), property.Name);
        }
    }

    [Fact, Trait("Category", "Editor")]
    public void The_page_is_a_com_visible_class_with_a_fixed_guid()
    {
        var page = typeof(DamlOptionPages.General);
        page.GetCustomAttribute<ComVisibleAttribute>()!.Value.ShouldBeTrue();
        page.GetCustomAttribute<GuidAttribute>()!.Value.ShouldBe("5d3a6f0e-8b2c-4e71-9a64-2f1c7b0d9e53");
    }

    // The page is registered with automation, so DTE.Properties("Daml", "General") hands the
    // page's settings out through IDispatch.
    [Fact, Trait("Category", "Editor")]
    public void The_settings_the_page_automates_can_be_handed_to_com()
    {
        Marshal.IsTypeVisibleFromCom(typeof(DamlOptions)).ShouldBeTrue();
        var dispatch = Marshal.GetIDispatchForObject(new DamlOptions());
        Marshal.Release(dispatch);
        typeof(IDamlGeneralOptions).GetProperties().Select(p => (p.Name, p.PropertyType)).OrderBy(p => p.Name)
            .ShouldBe(PageProperties().Select(p => (p.Name, p.PropertyType)).OrderBy(p => p.Name), "a new setting has to be added to the automation interface too");
    }

    [Fact, Trait("Category", "Editor")]
    public void Saves_before_the_first_start_ask_for_nothing()
    {
        var (monitor, save) = Monitor();
        var seen = Record(monitor);

        save(new DamlOptions { LogLevel = ServerLogLevel.Debug });

        seen.ShouldHaveSingleItem().RestartRequired.ShouldBeFalse();
        seen[0].Settings.LogLevel.ShouldBe(ServerLogLevel.Debug);
    }

    [Fact, Trait("Category", "Editor")]
    public void A_save_that_changes_the_server_command_asks_for_a_restart()
    {
        var (monitor, save) = Monitor();
        var seen = Record(monitor);
        monitor.MarkApplied(new DamlOptions().ToSettings());

        save(new DamlOptions { LogLevel = ServerLogLevel.Debug, ExtraArguments = "--x" });

        seen.ShouldHaveSingleItem().ChangesNeedingRestart.ShouldBe(["LogLevel", "ExtraArguments"]);
    }

    [Fact, Trait("Category", "Editor")]
    public void A_save_that_only_toggles_keep_alive_asks_for_no_restart_but_is_reported()
    {
        var (monitor, save) = Monitor();
        var seen = Record(monitor);
        monitor.MarkApplied(new DamlOptions().ToSettings());

        save(new DamlOptions { KeepAliveEnabled = false });

        var change = seen.ShouldHaveSingleItem();
        change.RestartRequired.ShouldBeFalse();
        change.Settings.KeepAliveEnabled.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Editor")]
    public void Restarting_with_the_saved_settings_clears_the_difference()
    {
        var (monitor, save) = Monitor();
        var seen = Record(monitor);
        monitor.MarkApplied(new DamlOptions().ToSettings());
        var edited = new DamlOptions { UseMultiIde = false };

        save(edited);
        monitor.MarkApplied(edited.ToSettings());
        save(edited);

        seen.Select(e => e.RestartRequired).ShouldBe([true, false]);
    }

    [Fact, Trait("Category", "Editor")]
    public void Dispose_stops_listening()
    {
        Action<DamlOptions>? handler = null;
        var monitor = new DamlSettingsMonitor(h => handler = h, h =>
        {
            h.ShouldBe(handler);
            handler = null;
        });

        monitor.Dispose();

        handler.ShouldBeNull();
    }

    private static (DamlSettingsMonitor Monitor, Action<DamlOptions> Save) Monitor()
    {
        Action<DamlOptions>? handler = null;
        var monitor = new DamlSettingsMonitor(h => handler = h, _ => handler = null);
        return (monitor, options => handler!(options));
    }

    private static List<DamlSettingsChangedEventArgs> Record(DamlSettingsMonitor monitor)
    {
        var seen = new List<DamlSettingsChangedEventArgs>();
        monitor.Changed += (_, e) => seen.Add(e);
        return seen;
    }
}
