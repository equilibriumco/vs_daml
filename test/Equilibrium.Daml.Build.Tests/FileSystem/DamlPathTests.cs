using Equilibrium.Daml.Build.Core.FileSystem;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.FileSystem;

public class DamlPathTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\app\", @"C:\work\app")]
    [InlineData(@"C:/work/app", @"C:\work\app")]
    [InlineData(@"C:\work\app\.\daml\..\x", @"C:\work\app\x")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_gives_one_canonical_spelling(string input, string expected) =>
        DamlPath.Normalize(input).ShouldBe(expected);

    // What a manifest records so a checkout elsewhere finds the same places; a path on another
    // drive or share can't be relative and stays as it is.
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\app", @"C:\work\app\Generated\Daml", @"Generated\Daml")]
    [InlineData(@"C:\work\app\obj\Debug\Daml\x-1", @"C:\work\app\App.csproj", @"..\..\..\..\App.csproj")]
    [InlineData(@"C:\work\App", @"c:\WORK\app\file.txt", "file.txt")]
    [InlineData(@"C:\work\app", @"C:\work\app", ".")]
    [InlineData(@"C:\work\app", @"C:\work\application\x", @"..\application\x")]
    [InlineData(@"C:\work\app", @"D:\vendor\a.dar", @"D:\vendor\a.dar")]
    [InlineData(@"\\server\share\app", @"\\server\other\a.dar", @"\\server\other\a.dar")]
    public void Relative_gives_the_way_from_a_folder_to_a_path(string from, string to, string expected)
    {
        DamlPath.Relative(from, to).ShouldBe(expected);
        DamlPath.Resolve(from, DamlPath.Relative(from, to)).ShouldBe(DamlPath.Normalize(to), StringCompareShould.IgnoreCase);
    }

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
    [InlineData(@"C:\work\repo", @"C:\", true)]
    public void IsSameOrUnder_compares_whole_segments_ignoring_case(string path, string directory, bool expected) =>
        DamlPath.IsSameOrUnder(path, directory).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:/", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\", true)]
    [InlineData(@"C:\work", false)]
    [InlineData(@"\\server\share\folder", false)]
    public void IsRoot_recognises_drive_and_share_roots(string path, bool expected) =>
        DamlPath.IsRoot(path).ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void Paths_compare_case_insensitively() =>
        DamlPath.Comparer.Equals(@"C:\Work\App", @"c:\work\app").ShouldBeTrue();

    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/Main.daml")]
    [InlineData(".")]
    public void Normalize_refuses_a_relative_path_rather_than_guessing_a_base(string input) =>
        Should.Throw<System.ArgumentException>(() => DamlPath.Normalize(input));

    // Rooted isn't full: C:bin is relative to the current folder of drive C, \bin to the current drive.
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\tools", true)]
    [InlineData("C:/tools", true)]
    [InlineData(@"\\server\share\tools", true)]
    [InlineData(@"C:tools", false)]
    [InlineData(@"\tools", false)]
    [InlineData(@"tools", false)]
    [InlineData(@".\tools", false)]
    [InlineData("C:", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_drive_with_its_root_or_a_share_makes_a_full_path(string? path, bool full) =>
        DamlPath.IsFullPath(path).ShouldBe(full);
}
