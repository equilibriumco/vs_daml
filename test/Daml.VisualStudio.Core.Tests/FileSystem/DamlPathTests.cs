using Daml.VisualStudio.Core.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.FileSystem;

public class DamlPathTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\app\", @"C:\work\app")]
    [InlineData(@"C:/work/app", @"C:\work\app")]
    [InlineData(@"C:\work\app\.\daml\..\x", @"C:\work\app\x")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_gives_one_canonical_spelling(string input, string expected) =>
        DamlPath.Normalize(input).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\repo", "./daml/test", @"C:\work\repo\daml\test")]
    [InlineData(@"C:\work\repo", "../shared", @"C:\work\shared")]
    [InlineData(@"C:\work\repo", @"D:\elsewhere\lib", @"D:\elsewhere\lib")]
    public void Resolve_handles_relative_forward_slash_and_rooted_paths(string baseDirectory, string path, string expected) =>
        DamlPath.Resolve(baseDirectory, path).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\repo\daml\Main.daml", @"C:\work\repo", true)]
    [InlineData(@"C:\WORK\Repo\daml", @"c:\work\repo", true)]
    [InlineData(@"C:\work\repo", @"C:\work\repo", true)]
    [InlineData(@"C:\work\repository\x", @"C:\work\repo", false)]
    [InlineData(@"C:\work", @"C:\work\repo", false)]
    public void IsSameOrUnder_compares_whole_segments_ignoring_case(string path, string directory, bool expected) =>
        DamlPath.IsSameOrUnder(path, directory).ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void Paths_compare_case_insensitively() =>
        DamlPath.Comparer.Equals(@"C:\Work\App", @"c:\work\app").ShouldBeTrue();

    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/Main.daml")]
    [InlineData(".")]
    public void Normalize_refuses_a_relative_path_rather_than_guessing_a_base(string input) =>
        Should.Throw<System.ArgumentException>(() => DamlPath.Normalize(input));
}
