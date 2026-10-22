using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Shouldly;
using VerifyXunit;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Server;

public class LanguageServerCommandBuilderTests
{
    private const string Dpm = @"C:\Users\ann\AppData\Roaming\dpm\bin\dpm.cmd";
    private const string Daml = @"C:\Users\ann\.daml\bin\daml.cmd";

    private static SdkResolution Sdk(AssistantKind kind = AssistantKind.Dpm, bool? multiIde = true) => new()
    {
        Assistant = new AssistantLocation(kind, kind == AssistantKind.Dpm ? Dpm : Daml),
        SupportsMultiIde = multiIde,
    };

    private static WorkspaceContext Workspace(ServerMode mode) => new()
    {
        WorkspaceRoot = @"C:\repo",
        ServerWorkingDirectory = mode == ServerMode.MultiIde ? @"C:\repo" : @"C:\repo\app",
        Mode = mode,
        PackageRoot = @"C:\repo\app",
        Packages = [@"C:\repo\app"],
        Version = 1,
    };

    private static IReadOnlyList<string> Arguments(ServerMode mode, LanguageServerOptions options) =>
        LanguageServerCommandBuilder.Build(Sdk(), Workspace(mode), options).Arguments;

    [Theory, Trait("Category", "Unit")]
    [InlineData(ServerLogLevel.Debug, "--log-level=DEBUG")]
    [InlineData(ServerLogLevel.Info, "--log-level=INFO")]
    [InlineData(ServerLogLevel.Warning, "--log-level=WARNING")]
    [InlineData(ServerLogLevel.Error, "--log-level=ERROR")]
    public void Multi_ide_gets_the_log_level_as_an_option(ServerLogLevel level, string expected) =>
        Arguments(ServerMode.MultiIde, new LanguageServerOptions { LogLevel = level }).ShouldBe(["damlc", "multi-ide", expected]);

    [Theory, Trait("Category", "Unit")]
    [InlineData(ServerLogLevel.Debug, new[] { "damlc", "ide", "--debug" })]
    [InlineData(ServerLogLevel.Info, new[] { "damlc", "ide" })]
    [InlineData(ServerLogLevel.Error, new[] { "damlc", "ide" })]
    public void Ide_only_gets_a_debug_switch(ServerLogLevel level, string[] expected) =>
        Arguments(ServerMode.Ide, new LanguageServerOptions { LogLevel = level }).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(ServerMode.Ide)]
    [InlineData(ServerMode.MultiIde)]
    public void Autorun_and_extra_arguments_come_after_the_built_in_ones(ServerMode mode)
    {
        var arguments = Arguments(mode, new LanguageServerOptions { AutorunAllTests = true, ExtraArguments = "--jobs=4 -W foo" });

        arguments.Skip(arguments.Count - 4).ShouldBe(["--studio-auto-run-all-scripts=yes", "--jobs=4", "-W", "foo"]);
    }

    // damlc ide still accepts --telemetry, --optOutTelemetry and --telemetry-ignored without
    // listing them; leaving them out keeps its usage reporting off.
    [Fact, Trait("Category", "Unit")]
    public void No_telemetry_switch_is_ever_added_by_the_extension()
    {
        var commands =
            from mode in new[] { ServerMode.Ide, ServerMode.MultiIde }
            from level in new[] { ServerLogLevel.Debug, ServerLogLevel.Info, ServerLogLevel.Warning, ServerLogLevel.Error }
            from autorun in new[] { false, true }
            from kind in new[] { AssistantKind.Dpm, AssistantKind.DamlAssistant }
            select LanguageServerCommandBuilder.Build(Sdk(kind), Workspace(mode), new LanguageServerOptions { LogLevel = level, AutorunAllTests = autorun });

        foreach (var command in commands)
        {
            command.Arguments.ShouldAllBe(argument => argument.IndexOf("telemetry", StringComparison.OrdinalIgnoreCase) < 0);
            command.Environment.ShouldBeEmpty();
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void The_server_runs_through_the_chosen_tool_in_the_server_working_folder_with_the_inherited_environment()
    {
        var spec = LanguageServerCommandBuilder.Build(Sdk(AssistantKind.DamlAssistant), Workspace(ServerMode.Ide), new LanguageServerOptions());

        spec.FileName.ShouldBe(Daml);
        spec.WorkingDirectory.ShouldBe(@"C:\repo\app");
        spec.Environment.ShouldBeEmpty();
        spec.Timeout.ShouldBeNull();
    }

    [Fact, Trait("Category", "Unit")]
    public void Multi_ide_on_a_damlc_without_it_is_refused()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => LanguageServerCommandBuilder.Build(Sdk(multiIde: false), Workspace(ServerMode.MultiIde), new LanguageServerOptions()));

        error.Message.ShouldContain("multi-ide");
    }

    [Fact, Trait("Category", "Unit")]
    public void Multi_ide_is_attempted_when_support_is_unknown() =>
        LanguageServerCommandBuilder.Build(Sdk(multiIde: null), Workspace(ServerMode.MultiIde), new LanguageServerOptions())
            .Arguments.ShouldContain("multi-ide");

    [Theory, Trait("Category", "Unit")]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("--a  --b\t-c", new[] { "--a", "--b", "-c" })]
    [InlineData("--include \"C:\\my dir\\lib\" -W x", new[] { "--include", @"C:\my dir\lib", "-W", "x" })]
    [InlineData("--ghc-option=\"-O2 -v\"", new[] { "--ghc-option=-O2 -v" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("\"unterminated value", new[] { "unterminated value" })]
    public void Extra_arguments_split_on_whitespace_and_honour_quotes(string? text, string[] expected) =>
        LanguageServerCommandBuilder.SplitArguments(text).ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public Task Command_lines_for_the_usual_combinations()
    {
        var debugWithExtras = new LanguageServerOptions { LogLevel = ServerLogLevel.Debug, AutorunAllTests = true, ExtraArguments = "--jobs=2" };
        var cases = new Dictionary<string, object>
        {
            ["multi-ide, dpm, defaults"] = LanguageServerCommandBuilder.Build(Sdk(), Workspace(ServerMode.MultiIde), new LanguageServerOptions()),
            ["multi-ide, dpm, debug + autorun + extras"] = LanguageServerCommandBuilder.Build(Sdk(), Workspace(ServerMode.MultiIde), debugWithExtras),
            ["multi-ide, daml assistant, error"] = LanguageServerCommandBuilder.Build(Sdk(AssistantKind.DamlAssistant), Workspace(ServerMode.MultiIde), new LanguageServerOptions { LogLevel = ServerLogLevel.Error }),
            ["ide, dpm, defaults"] = LanguageServerCommandBuilder.Build(Sdk(), Workspace(ServerMode.Ide), new LanguageServerOptions()),
            ["ide, dpm, debug + autorun + extras"] = LanguageServerCommandBuilder.Build(Sdk(), Workspace(ServerMode.Ide), debugWithExtras),
            ["ide, daml assistant, warning"] = LanguageServerCommandBuilder.Build(Sdk(AssistantKind.DamlAssistant), Workspace(ServerMode.Ide), new LanguageServerOptions { LogLevel = ServerLogLevel.Warning }),
        };
        return Verifier.Verify(cases);
    }
}
