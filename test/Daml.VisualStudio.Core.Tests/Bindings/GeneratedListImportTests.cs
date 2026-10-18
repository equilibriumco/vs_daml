using System;
using System.Linq;
using Daml.VisualStudio.Core.Bindings;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Bindings;

// Visual Studio watches only the files an evaluation imported; a list of generated files written
// after an evaluation that found none is missed until the project is evaluated again.
public class GeneratedListImportTests
{
    private const string List = @"C:\sln\App\obj\daml\App.csproj.Debug.net10.0.g.props";

    private static readonly string[] Imports = [@"C:\Program Files\dotnet\sdk\10.0.402\Sdks\Microsoft.NET.Sdk\Sdk\Sdk.props", @"C:\sln\App\obj\App.csproj.nuget.g.props"];

    private static bool Exists(string path) => string.Equals(path, List, StringComparison.OrdinalIgnoreCase);

    [Fact, Trait("Category", "Unit")]
    public void A_list_written_after_the_evaluation_is_missed()
    {
        GeneratedListImport.Missed(List, Imports, Exists).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_list_the_evaluation_imported_is_watched()
    {
        GeneratedListImport.Missed(List, Imports.Append(List.ToUpperInvariant().Replace('\\', '/')), Exists).ShouldBeFalse();
    }

    // Before the first build of all, or a project whose bindings were never generated.
    [Fact, Trait("Category", "Unit")]
    public void No_list_on_disk_is_nothing_missed()
    {
        GeneratedListImport.Missed(List, Imports, _ => false).ShouldBeFalse();
    }

    // A project without the package, or the outer evaluation of one with several target frameworks.
    [Theory, Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_project_that_names_no_list_misses_none(string? list)
    {
        GeneratedListImport.Missed(list, Imports, _ => true).ShouldBeFalse();
    }
}
