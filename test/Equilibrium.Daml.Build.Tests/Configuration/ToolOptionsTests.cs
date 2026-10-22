using System;
using System.Collections.Generic;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Configuration;

public class ToolOptionsTests
{
    private static (ToolOptions Options, List<ConfigurationError> Errors) Read(params (string Name, string Value)[] properties)
    {
        var values = new Dictionary<string, string>();
        foreach (var (name, value) in properties)
        {
            values[name] = value;
        }
        var errors = new List<ConfigurationError>();
        return (ToolOptions.Read(name => values.TryGetValue(name, out var value) ? value : null, errors), errors);
    }

    [Fact, Trait("Category", "Unit")]
    public void Unset_properties_give_the_defaults()
    {
        var (options, errors) = Read();

        errors.ShouldBeEmpty();
        options.ShouldBe(new ToolOptions(
            DpmPath: null, Offline: false, AllowToolInstall: false, SkipPreflight: false,
            ProcessTimeout: TimeSpan.FromSeconds(1800), LockTimeout: TimeSpan.FromSeconds(600)));
    }

    [Fact, Trait("Category", "Unit")]
    public void Set_properties_are_read()
    {
        var (options, errors) = Read(
            ("EqDamlDpmPath", @"C:\tools\dpm.cmd"), ("EqDamlOffline", "true"), ("EqDamlSkipPreflight", "yes"),
            ("EqDamlProcessTimeoutSeconds", " 30 "), ("EqDamlLockTimeoutSeconds", "5"));

        errors.ShouldBeEmpty();
        options.ShouldBe(new ToolOptions(
            DpmPath: @"C:\tools\dpm.cmd", Offline: true, AllowToolInstall: false, SkipPreflight: true,
            ProcessTimeout: TimeSpan.FromSeconds(30), LockTimeout: TimeSpan.FromSeconds(5)));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_process_timeout_of_zero_means_no_limit() =>
        Read(("EqDamlProcessTimeoutSeconds", "0")).Options.ProcessTimeout.ShouldBeNull();

    [Theory, Trait("Category", "Unit")]
    [InlineData("EqDamlOffline", "maybe")]
    [InlineData("EqDamlAllowToolInstall", "1")]
    [InlineData("EqDamlSkipPreflight", "sure")]
    [InlineData("EqDamlProcessTimeoutSeconds", "-1")]
    [InlineData("EqDamlProcessTimeoutSeconds", "ten")]
    [InlineData("EqDamlProcessTimeoutSeconds", "1.5")]
    [InlineData("EqDamlLockTimeoutSeconds", "0")]
    [InlineData("EqDamlLockTimeoutSeconds", "99999999999")]
    public void A_value_out_of_range_is_a_configuration_error_naming_the_property(string name, string value)
    {
        var errors = Read((name, value)).Errors;

        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.InvalidConfiguration);
        error.Message.ShouldContain(name);
        error.Message.ShouldContain($"'{value}'");
    }

    // dpm installs nothing unless asked, but the variable is always set: a user's own
    // DPM_AUTO_INSTALL must not decide what an offline build does.
    [Theory, Trait("Category", "Unit")]
    [InlineData("false", "false", "false")]
    [InlineData("false", "true", "true")]
    [InlineData("true", "false", "false")]
    [InlineData("true", "true", "false")]
    public void Dpm_installs_tools_only_when_the_build_allows_it(string offline, string install, string expected) =>
        Read(("EqDamlOffline", offline), ("EqDamlAllowToolInstall", install)).Options.DpmEnvironment
            .ShouldBe(new Dictionary<string, string> { ["DPM_AUTO_INSTALL"] = expected });

    [Fact, Trait("Category", "Unit")]
    public void Offline_overrides_permission_to_install()
    {
        var read = Read(("EqDamlOffline", "true"), ("EqDamlAllowToolInstall", "true"));

        read.Errors.ShouldBeEmpty();
        read.Options.Offline.ShouldBeTrue();
        read.Options.AllowToolInstall.ShouldBeFalse();
    }
}
