using System.Collections.Generic;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Tools;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tools;

public class JavaTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("java version \"26.0.2.1\" 2026-08-18\r\nJava(TM) SE Runtime Environment (build 26.0.2.1+1-7)\r\n", 26)]
    [InlineData("openjdk version \"21.0.12\" 2025-10-21 LTS\nOpenJDK Runtime Environment Temurin-21.0.12+7\n", 21)]
    [InlineData("openjdk version \"17\" 2021-09-14\n", 17)]
    [InlineData("java version \"1.8.0_391\"\n", 8)]
    [InlineData("Picked up JAVA_TOOL_OPTIONS: -Dfile.encoding=UTF-8\nopenjdk version \"17.0.20\" 2026-07-15\n", 17)]
    public void The_major_version_is_read_from_java_version(string output, int major) =>
        JavaVersion.Parse(output.Split('\n')).ShouldBe(major);

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("Error: could not open `C:\\jdk\\lib\\jvm.cfg'\n")]
    public void Output_without_a_version_gives_none(string output) =>
        JavaVersion.Parse(output.Split('\n')).ShouldBeNull();

    [Fact, Trait("Category", "Unit")]
    public void JDK_17_and_later_pass_and_older_ones_are_EQD0102()
    {
        JavaVersion.Problem(17, @"C:\jdk\bin\java.exe", []).ShouldBeNull();
        JavaVersion.Problem(26, @"C:\jdk\bin\java.exe", []).ShouldBeNull();

        var problem = JavaVersion.Problem(11, @"C:\jdk-11\bin\java.exe", []).ShouldNotBeNull();
        problem.Code.ShouldBe(DamlDiagnosticCodes.MissingJava);
        problem.Message.ShouldBe(
            @"Java 11 (C:\jdk-11\bin\java.exe) is older than 17, which the C# code generator needs. Install JDK 17 or later and point EqDamlJavaHome or JAVA_HOME at it.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_java_that_cant_say_its_version_quotes_what_it_printed() =>
        JavaVersion.Problem(null, @"C:\jdk\bin\java.exe", ["", "Error: could not open jvm.cfg"])!.Message.ShouldBe(
            "'C:\\jdk\\bin\\java.exe -version' printed no version, so the build can't tell whether it is JDK 17 or later. It printed:\nError: could not open jvm.cfg");

    private readonly InMemoryFileSystem _files = new();
    private readonly Dictionary<string, string> _environment = new() { ["PATH"] = @"C:\Windows;C:\tools\jdk\bin" };
    private readonly List<ConfigurationError> _errors = [];

    private string? Find(string? javaHome) =>
        JavaLocation.Find(javaHome, name => _environment.TryGetValue(name, out var value) ? value : null, _files, _errors);

    // The generator's wrapper runs whatever java is first on PATH; the build puts this one there.
    [Fact, Trait("Category", "Unit")]
    public void A_set_JDK_folder_wins_over_PATH()
    {
        _files.AddFile(@"C:\jdk-21\bin\java.exe", "").AddFile(@"C:\tools\jdk\bin\java.exe", "");

        Find(@"C:\jdk-21\").ShouldBe(@"C:\jdk-21\bin\java.exe");
        Find(null).ShouldBe(@"C:\tools\jdk\bin\java.exe");
        _errors.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_JDK_folder_without_java_is_an_error_not_a_fallback()
    {
        _files.AddFile(@"C:\tools\jdk\bin\java.exe", "");

        Find(@"C:\jre-missing").ShouldBeNull();

        var error = _errors.ShouldHaveSingleItem();
        (error.Code, error.Message).ShouldBe((DamlDiagnosticCodes.MissingJava,
            @"EqDamlJavaHome is 'C:\jre-missing', but C:\jre-missing\bin\java.exe doesn't exist. Point it, or JAVA_HOME, at a JDK 17 or later."));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_JDK_folder_that_isnt_a_full_path_is_an_error()
    {
        Find(@"tools\jdk").ShouldBeNull();

        _errors.ShouldHaveSingleItem().Message.ShouldBe(@"EqDamlJavaHome is 'tools\jdk', which isn't the full path of a JDK folder.");
    }

    [Fact, Trait("Category", "Unit")]
    public void No_java_anywhere_is_EQD0102_saying_what_to_do()
    {
        Find("").ShouldBeNull();

        var error = _errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DamlDiagnosticCodes.MissingJava);
        error.Message.ShouldBe(
            "The C# code generator needs Java, and none was found: EqDamlJavaHome and JAVA_HOME are empty and no java.exe is on PATH. Install JDK 17 or later.");
    }
}
