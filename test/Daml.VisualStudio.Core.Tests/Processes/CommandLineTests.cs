using System;
using Daml.VisualStudio.Core.Processes;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Processes;

public class CommandLineTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    public void Executable_arguments_follow_the_CommandLineToArgv_rules(string argument, string expected) =>
        CommandLine.ForExecutable([argument]).ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void Batch_files_run_through_cmd_with_every_argument_quoted()
    {
        var line = CommandLine.ForBatch(@"C:\tools\dpm.cmd", ["versions", "-o", "json"]);

        line.ShouldBe("/d /s /v:off /c \"\"C:\\tools\\dpm.cmd\" \"versions\" \"-o\" \"json\"\"");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("100%", "\"100%%cd:~,%\"")]
    [InlineData("x\"y", "\"x\"\"y\"")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData("a&b|c", "\"a&b|c\"")]
    public void Batch_arguments_survive_cmd_expansion(string argument, string expected) =>
        CommandLine.ForBatch("x.cmd", [argument]).ShouldEndWith(" " + expected + "\"");

    [Theory, Trait("Category", "Unit")]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("nul\0char")]
    public void Batch_arguments_cmd_would_cut_short_are_refused(string argument) =>
        Should.Throw<ArgumentException>(() => CommandLine.ForBatch("x.cmd", [argument]));

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\tools\dpm.cmd", true)]
    [InlineData(@"C:\tools\DAML.BAT", true)]
    [InlineData(@"C:\tools\dpm.exe", false)]
    [InlineData("dpm", false)]
    public void Batch_files_are_told_apart_by_extension(string fileName, bool expected) =>
        CommandLine.IsBatchFile(fileName).ShouldBe(expected);
}
