using System.Collections.Generic;
using Equilibrium.Daml.Build.Core.Building;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Building;

public class DarConfirmationTests
{
    private const string Manifest = @"C:\work\Contracts\daml.yaml";
    private const string Expected = @"C:\work\Contracts\.daml\dist\contracts-1.2.0.dar";

    private readonly List<BuildMessage> _problems = [];

    private string? Confirm(string? created, bool exists = false) =>
        DarConfirmation.Confirm(new DpmOutcome([], created), Expected, Manifest, _ => exists, _problems);

    [Fact, Trait("Category", "Unit")]
    public void The_DAR_dpm_created_where_the_manifest_says_is_the_result()
    {
        Confirm(@"c:\WORK\Contracts\.daml\dist\contracts-1.2.0.dar").ShouldBe(@"c:\WORK\Contracts\.daml\dist\contracts-1.2.0.dar");
        _problems.ShouldBeEmpty();
    }

    // Projects using the DAR take it from where the manifest says; a DAR elsewhere would leave
    // them reading a stale one.
    [Fact, Trait("Category", "Unit")]
    public void A_DAR_somewhere_else_is_a_configuration_error()
    {
        Confirm(@"C:\work\Contracts\out\other.dar").ShouldBeNull();

        var problem = _problems.ShouldHaveSingleItem();
        (problem.Code, problem.File).ShouldBe((DamlDiagnosticCodes.InvalidConfiguration, Manifest));
        problem.Text.ShouldBe($@"dpm wrote the DAR to C:\work\Contracts\out\other.dar, but daml.yaml says it goes to {Expected}.");
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_Created_line_the_DAR_on_disk_counts()
    {
        Confirm(null, exists: true).ShouldBe(Expected);
        _problems.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_successful_build_without_a_DAR_is_an_error()
    {
        Confirm(null).ShouldBeNull();

        var problem = _problems.ShouldHaveSingleItem();
        (problem.Code, problem.Text).ShouldBe((DamlDiagnosticCodes.CompileError, $"dpm build succeeded but left no DAR at {Expected}."));
    }
}
