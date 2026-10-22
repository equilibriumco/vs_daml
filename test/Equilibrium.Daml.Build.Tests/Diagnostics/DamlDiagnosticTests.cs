using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Diagnostics;

public class DamlDiagnosticTests
{
    private static DamlDiagnostic Printed(string path, string? packageRoot = null) => new() { PrintedPath = path, PackageRoot = packageRoot };

    [Theory, Trait("Category", "Unit")]
    [InlineData("daml/Main.daml", null, @"C:\work\app\daml\Main.daml")]
    [InlineData(@"C:\other\X.daml", null, @"C:\other\X.daml")]
    [InlineData("daml/Main.daml", @"C:\work\repo\app", @"C:\work\repo\app\daml\Main.daml")]
    [InlineData("daml/Main.daml", "app", @"C:\work\app\app\daml\Main.daml")]
    public void The_printed_path_resolves_under_the_package_then_the_working_folder(string printed, string? packageRoot, string expected) =>
        Printed(printed, packageRoot).ResolvePath(@"C:\work\app").ShouldBe(expected);

    // damlc has no file to blame for a package-level problem (missing dependency, bad option).
    [Theory, Trait("Category", "Unit")]
    [InlineData("<unknown>")]
    [InlineData("<no location info>")]
    public void A_stand_in_instead_of_a_file_resolves_to_the_package_folder(string printed)
    {
        Printed(printed).ResolvePath(@"C:\work\app").ShouldBe(@"C:\work\app");
        Printed(printed, @"C:\work\repo\lib").ResolvePath(@"C:\work\repo").ShouldBe(@"C:\work\repo\lib");
    }
}
