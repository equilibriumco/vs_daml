using Shouldly;
using Xunit;

namespace Equilibrium.Daml.TestHelpers.Tests;

public class RequiredToolsTests
{
    [Fact, Trait("Category", "Unit")]
    public void Returns_the_path_when_the_tool_is_present()
    {
        RequiredTools.Require(ExternalTool.Dpm, @"C:\dpm\bin\dpm.cmd", requiredMode: true).ShouldBe(@"C:\dpm\bin\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void Missing_tool_skips_the_test_in_local_runs()
    {
        var ex = Should.Throw<SkipException>(() => RequiredTools.Require(ExternalTool.Dpm, path: null, requiredMode: false));
        ex.Message.ShouldContain("dpm");
    }

    [Fact, Trait("Category", "Unit")]
    public void Missing_tool_fails_the_test_in_gate_runs()
    {
        var ex = Should.Throw<RequiredToolMissingException>(() => RequiredTools.Require(ExternalTool.Java, path: null, requiredMode: true));
        ex.Message.ShouldContain("java");
        ex.Message.ShouldContain(RequiredTools.EnvironmentVariable);
    }
}
