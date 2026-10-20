using System;
using Daml.Build.Core.Manifests;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Manifests;

public class PackageEnvironmentTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("$SRC/${SRC}", "daml/daml")]
    [InlineData("$SRC.more", "daml.more")]
    [InlineData("${}", "")]
    [InlineData("${unfinished", "unfinished")]
    [InlineData("$", "$")]
    [InlineData("$.", "$.")]
    [InlineData("$EMPTY", "")]
    public void Raw_expansion_matches_Go_shell_name_rules(string input, string expected) =>
        PackageEnvironment.Expand(input, name => name switch { "SRC" => "daml", "EMPTY" => "", _ => null }).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData("$MISSING")]
    [InlineData("$$")]
    [InlineData("$1")]
    public void Unset_variables_including_shell_special_names_are_errors(string input) =>
        Should.Throw<FormatException>(() => PackageEnvironment.Expand(input, _ => null));
}
