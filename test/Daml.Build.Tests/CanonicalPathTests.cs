using Daml.Build.Core;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests;

public class CanonicalPathTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"obj\Debug\net10.0\", @"C:\work\App", @"C:\work\App\obj\Debug\net10.0")]
    [InlineData(@"..\Contracts\Contracts.msbuildproj", @"C:\work\App", @"C:\work\Contracts\Contracts.msbuildproj")]
    [InlineData(@"C:/vendor/./dars/../finance.dar", @"C:\work\App", @"C:\vendor\finance.dar")]
    [InlineData(@"D:\out\", @"C:\work\App", @"D:\out")]
    [InlineData(@"C:\", @"C:\work\App", @"C:\")]
    public void Paths_resolve_against_the_project_folder_without_a_trailing_separator(string path, string baseFolder, string expected) =>
        CanonicalPath.Resolve(path, baseFolder).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\App\obj", @"C:\work\App", true)]
    [InlineData(@"c:\WORK\app\obj\Debug", @"C:\work\App", true)]
    [InlineData(@"C:\work\App", @"C:\work\App", true)]
    [InlineData(@"C:\work\AppTests", @"C:\work\App", false)]
    [InlineData(@"C:\work", @"C:\work\App", false)]
    [InlineData(@"C:\work\App", @"C:\", true)]
    public void A_folder_contains_itself_and_its_subfolders_in_any_letter_case(string path, string folder, bool expected) =>
        CanonicalPath.IsSameOrInside(path, folder).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"C:\work", false)]
    [InlineData(@"\\server\share\folder", false)]
    public void Drive_and_share_roots_are_recognised(string path, bool expected) =>
        CanonicalPath.IsRoot(path).ShouldBe(expected);
}
