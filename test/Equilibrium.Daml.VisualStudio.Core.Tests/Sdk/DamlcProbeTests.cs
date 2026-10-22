using System;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Sdk;

public class DamlcProbeTests
{
    private const string Dpm = @"C:\dpm\bin\dpm.cmd";
    private static readonly AssistantLocation Assistant = new(AssistantKind.Dpm, Dpm);

    // Captured from `dpm damlc --help` with SDK 3.6.0 (damlc 3.6.2); trimmed after the commands of interest.
    internal const string TopHelp = """
        damlc - Compiler and IDE backend for the Daml programming language
        SDK Version: 3.6.2

        Usage: damlc.exe COMMAND

          Invoke the Daml compiler. Use -h for help.

        Available options:
          -h,--help                Show this help text

        Available commands:
          ide                      Start the Daml language server on standard
                                   input/output.
          multi-ide                Start the Daml Multi-IDE language server on standard
                                   input/output.
          upgrade-check            Check upgrades for multiple DARs
          build                    Initialize, build and package the Daml package
        """;

    // Captured from `dpm damlc multi-ide --help`; identical on SDK 3.5.2 and 3.6.0.
    internal const string MultiIdeHelp = """
        Usage: damlc.exe multi-ide [--debug | [--log-level ARG]] [--ide-identifier ARG]

          Start the Daml Multi-IDE language server on standard input/output.

        Available options:
          -h,--help                Show this help text
          --debug                  Set log level to DEBUG
          --log-level ARG          Set log level. Possible values are DEBUG, INFO,
                                   WARNING, ERROR
          --ide-identifier ARG     Identifier string for this IDE
        """;

    private const string OldTopHelp = """
        damlc - Compiler and IDE backend for the Daml programming language
        SDK Version: 2.8.0

        Available commands:
          ide                      Start the Daml language server on standard
                                   input/output.
          build                    Initialize, build and package the Daml package
        """;

    private readonly FakeProcessRunner _runner = new();

    private Task<DamlcCapabilities> ProbeAsync() =>
        new DamlcProbe(_runner).ProbeAsync(Assistant, @"C:\repo", CancellationToken.None);

    [Fact, Trait("Category", "Unit")]
    public async Task The_running_damlc_reports_its_version_and_both_server_modes()
    {
        _runner.On(Dpm, ["damlc", "--help"], TopHelp);
        _runner.On(Dpm, ["damlc", "multi-ide", "--help"], MultiIdeHelp);

        var capabilities = await ProbeAsync();

        capabilities.ShouldBe(new DamlcCapabilities("3.6.2", SupportsIde: true, SupportsMultiIde: true, SupportsIdeIdentifier: true));
        _runner.Calls.ShouldAllBe(c => c.WorkingDirectory == @"C:\repo");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_damlc_without_multi_ide_is_not_asked_about_its_flags()
    {
        _runner.On(Dpm, ["damlc", "--help"], OldTopHelp);

        var capabilities = await ProbeAsync();

        capabilities.ShouldBe(new DamlcCapabilities("2.8.0", SupportsIde: true, SupportsMultiIde: false, SupportsIdeIdentifier: false));
        _runner.Calls.Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Multi_ide_without_the_identifier_flag_is_reported_as_such()
    {
        _runner.On(Dpm, ["damlc", "--help"], TopHelp);
        _runner.On(Dpm, ["damlc", "multi-ide", "--help"], "Usage: damlc.exe multi-ide [--debug | [--log-level ARG]]\n");

        (await ProbeAsync()).SupportsIdeIdentifier.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failing_help_command_is_an_error_for_the_caller()
    {
        _runner.On(Dpm, ["damlc", "--help"], new ProcessResult(1, "", "SDK 9.9.9 is not installed"));

        var error = await Should.ThrowAsync<InvalidOperationException>(ProbeAsync);

        error.Message.ShouldContain("SDK 9.9.9 is not installed");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Help_that_is_not_damlc_help_is_a_format_error()
    {
        _runner.On(Dpm, ["damlc", "--help"], "Something else entirely\n");

        await Should.ThrowAsync<FormatException>(ProbeAsync);
    }
}
