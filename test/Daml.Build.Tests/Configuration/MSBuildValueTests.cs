using Daml.Build.Core.Configuration;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Configuration;

public class MSBuildValueTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" on ", true)]
    [InlineData("Yes", true)]
    [InlineData("!false", true)]
    [InlineData("!no", true)]
    [InlineData("false", false)]
    [InlineData("Off", false)]
    [InlineData("no", false)]
    [InlineData("!true", false)]
    [InlineData("!ON", false)]
    public void Booleans_read_like_msbuild_reads_them(string text, bool expected) =>
        MSBuildValue.Bool(text, defaultValue: !expected).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_boolean_takes_the_default(string? text)
    {
        MSBuildValue.Bool(text, defaultValue: true).ShouldBe(true);
        MSBuildValue.Bool(text, defaultValue: false).ShouldBe(false);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("!!true")]
    [InlineData("true!")]
    public void Anything_else_is_not_a_boolean(string text) =>
        MSBuildValue.Bool(text, defaultValue: false).ShouldBeNull();
}
