using Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Diagnostics;

public class DarCreatedLineTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"Created .daml\dist\single-package-0.0.1.dar", @".daml\dist\single-package-0.0.1.dar")]
    [InlineData("Created custom\\my.dar  \r", @"custom\my.dar")]
    [InlineData("\u001b[0;32mCreated out/x.dar\u001b[0m", "out/x.dar")]
    [InlineData(@"Created D:\dars\z.dar", @"D:\dars\z.dar")]
    public void The_line_dpm_ends_a_build_with_names_the_DAR(string line, string dar) =>
        DarCreatedLine.TryParse(line).ShouldBe(dar);

    [Theory, Trait("Category", "Unit")]
    [InlineData("Compiling single-package to a DAR.")]
    [InlineData("Created")]
    [InlineData("Created a new package")]
    [InlineData("  Created x.dar")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_lines_name_nothing(string? line) =>
        DarCreatedLine.TryParse(line).ShouldBeNull();
}
