using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Daml.VisualStudio.Bindings;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// "C#: N references" and its window: one source and one window for the session, the window
// registered with the package, and its rows going to Visual Studio's table as the table expects them.
public class CSharpReferencesExportsTests
{
    [Theory, Trait("Category", "Editor")]
    [InlineData(typeof(RoslynCSharpReferenceSource))]
    [InlineData(typeof(CSharpReferencesWindow))]
    public async Task One_reference_source_and_one_window_serve_every_Daml_editor(Type part)
    {
        var parts = await MefParts.DiscoverAsync();

        MefParts.PartsExporting(parts, part).ShouldHaveSingleItem().IsShared.ShouldBeTrue();
    }

    // The table control finds a row's navigation by the manager its window's table belongs to.
    [Fact, Trait("Category", "Editor")]
    public async Task Rows_of_the_references_window_navigate_through_their_own_table_manager()
    {
        var export = (await MefParts.ExportsAsync(typeof(ITableControlEventProcessorProvider))).ShouldHaveSingleItem();

        export.Metadata.Values.SelectMany(Values).ShouldContain(CSharpReferencesToolWindow.ManagerIdentifier);
    }

    [Fact, Trait("Category", "Editor")]
    public void The_package_registers_the_references_window()
    {
        typeof(DamlPackage).GetCustomAttributes<ProvideToolWindowAttribute>().ShouldContain(a => a.ToolType == typeof(CSharpReferencesToolWindow));
    }

    // The table counts lines and columns from zero; the references from one.
    [Fact, Trait("Category", "Editor")]
    public void A_row_gives_the_table_project_file_line_column_and_code()
    {
        var entry = new CSharpReferenceEntry(new CSharpReference("App", @"C:\sln\App\Program.cs", 6, 13, 120, 5, "var asset = new Asset(alice, bob, \"Gold\");"));

        Value(entry, StandardTableKeyNames.ProjectName).ShouldBe("App");
        Value(entry, StandardTableKeyNames.DocumentName).ShouldBe(@"C:\sln\App\Program.cs");
        Value(entry, StandardTableKeyNames.Line).ShouldBe(5);
        Value(entry, StandardTableKeyNames.Column).ShouldBe(12);
        Value(entry, StandardTableKeyNames.Text).ShouldBe("var asset = new Asset(alice, bob, \"Gold\");");
        entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out _).ShouldBeFalse();
        entry.CanSetValue(StandardTableKeyNames.Text).ShouldBeFalse();
    }

    private static object Value(CSharpReferenceEntry entry, string key) =>
        entry.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    private static IEnumerable<string?> Values(object? metadata) =>
        metadata switch
        {
            string single => [single],
            IEnumerable<string> many => many,
            _ => [],
        };
}
