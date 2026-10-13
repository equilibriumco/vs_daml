using System;
using Daml.VisualStudio.Core.Sdk;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Sdk;

public class VersionOutputTests
{
    // Captured from dpm 1.0.22 with SDKs 3.5.2 and 3.6.0 installed.
    private const string DpmVersionsJson = """
        [
            {
                "version": "3.5.2",
                "installed": true
            },
            {
                "version": "3.6.0",
                "installed": true,
                "active": true
            }
        ]
        """;

    [Fact, Trait("Category", "Unit")]
    public void The_active_dpm_sdk_is_the_entry_marked_active()
    {
        DpmSdkResolver.ParseActiveVersion(DpmVersionsJson).ShouldBe("3.6.0");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("""[{"version":"3.6.0","installed":true}]""")]
    [InlineData("[]")]
    public void Without_an_active_entry_there_is_no_active_version(string json) =>
        DpmSdkResolver.ParseActiveVersion(json).ShouldBeNull();

    [Theory, Trait("Category", "Unit")]
    [InlineData("Error: something broke")]
    [InlineData("""{"version":"3.6.0"}""")]
    [InlineData("""[{"version":3}]""")]
    public void Output_that_is_not_the_expected_list_is_a_format_error(string json) =>
        Should.Throw<FormatException>(() => DpmSdkResolver.ParseActiveVersion(json));

    // `daml version` output is not captured here (no daml assistant on the dev machine); the lines
    // follow the labels the official VS Code extension matches on.
    [Fact, Trait("Category", "Unit")]
    public void The_daml_assistant_version_prefers_the_env_var_then_the_project_then_the_default()
    {
        const string All = """
            SDK versions:
              2.10.0  (selected by env var DAML_SDK_VERSION)
              2.9.4   (project SDK version from daml.yaml)
              2.8.0   (default SDK version for new projects)
              2.7.1
            """;
        const string Project = """
            SDK versions:
              2.9.4   (project SDK version from daml.yaml, default SDK version for new projects)
              2.8.0
            """;
        const string Fallback = """
            SDK versions:
              2.8.0   (default SDK version for new projects)
              2.7.1   (not installed)
            """;

        DamlAssistantSdkResolver.ParseSelectedVersion(All).ShouldBe("2.10.0");
        DamlAssistantSdkResolver.ParseSelectedVersion(Project).ShouldBe("2.9.4");
        DamlAssistantSdkResolver.ParseSelectedVersion(Fallback).ShouldBe("2.8.0");
        DamlAssistantSdkResolver.ParseSelectedVersion("SDK versions:\n  2.7.1\n").ShouldBeNull();
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("3.3.0", 3, 4, true)]
    [InlineData("3.4.0", 3, 4, false)]
    [InlineData("2.10.1", 3, 4, true)]
    [InlineData("3.3.0-snapshot.20250312.0", 3, 4, true)]
    [InlineData("3.6.0", 2, 9, false)]
    public void Sdk_versions_compare_numerically(string version, int major, int minor, bool before) =>
        SdkVersion.IsBefore(version, major, minor).ShouldBe(before);

    [Theory, Trait("Category", "Unit")]
    [InlineData("0.0.0")]
    [InlineData("latest")]
    [InlineData("")]
    public void Snapshot_and_unparsable_versions_are_unknown(string version) =>
        SdkVersion.IsBefore(version, 3, 4).ShouldBeNull();
}
