using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Daml.TestHelpers;
using Daml.VisualStudio.Bindings;
using Daml.VisualStudio.Commands;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Editor.Commanding;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text.Editor;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

// Going from C# to Daml lives in C# editors only, wired by MEF and the command table without the package.
public class BindingNavigationExportsTests
{
    private static readonly XNamespace Vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
    private static readonly XDocument Table = XDocument.Load(Path.Combine(Repository.Root, "src", "Daml.VisualStudio", "DamlPackage.vsct"));

    private static IEnumerable<string> ContentTypes(IReadOnlyDictionary<string, object?> metadata) =>
        metadata["ContentTypes"] switch
        {
            string single => [single],
            IEnumerable<string> many => many,
            _ => [],
        };

    [Fact, Trait("Category", "Editor")]
    public async Task The_quick_info_source_is_for_CSharp()
    {
        var export = (await MefParts.ExportsAsync(typeof(IAsyncQuickInfoSourceProvider))).ShouldHaveSingleItem();

        ContentTypes(export.Metadata).ShouldBe(["CSharp"]);
        export.Metadata["Name"].ShouldBe("Daml bindings");
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_command_is_handled_in_CSharp_editors()
    {
        var export = (await MefParts.ExportsAsync(typeof(ICommandHandler))).Single(e => (string?)e.Metadata["Name"] == nameof(GoToDamlDefinitionCommandHandler));

        ContentTypes(export.Metadata).ShouldBe(["CSharp"]);
    }

    // The binding is what routes the command from the context menu to the editor's handler.
    [Fact, Trait("Category", "Editor")]
    public async Task The_command_is_bound_to_the_editor_by_its_id()
    {
        var export = (await MefParts.ExportsAsync(typeof(CommandBindingDefinition))).ShouldHaveSingleItem();

        // The binding attribute may be applied several times, so each of its values is a list.
        var values = export.Metadata.Values.SelectMany(v => v is System.Collections.IEnumerable list and not string ? list.Cast<object?>() : [v])
            .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).ToList();
        values.ShouldContain(v => string.Equals(v, DamlMenuIds.CommandSetString, StringComparison.OrdinalIgnoreCase));
        values.ShouldContain(((uint)DamlMenuIds.GoToDamlDefinition).ToString(CultureInfo.InvariantCulture));
        values.ShouldContain(typeof(GoToDamlDefinitionCommandArgs).AssemblyQualifiedName);
    }

    [Fact, Trait("Category", "Editor")]
    public async Task The_caret_is_followed_in_CSharp_documents()
    {
        var export = (await MefParts.ExportsAsync(typeof(IWpfTextViewCreationListener))).Single(e => ContentTypes(e.Metadata).Contains("CSharp"));

        ((IEnumerable<string>)export.Metadata["TextViewRoles"]!).ShouldBe([PredefinedTextViewRoles.Document]);
    }

    [Theory, Trait("Category", "Editor")]
    [InlineData(typeof(DamlBindingService))]
    [InlineData(typeof(DamlNavigator))]
    public async Task One_index_and_one_navigator_serve_every_editor(Type part)
    {
        var parts = await MefParts.DiscoverAsync();

        var exporting = MefParts.PartsExporting(parts, part).ShouldHaveSingleItem();
        exporting.IsShared.ShouldBeTrue();
    }

    // Hidden unless a C# editor's handler says the name at the caret stands for Daml; no key of its own.
    [Fact, Trait("Category", "Editor")]
    public void The_button_sits_with_Go_To_Definition_and_is_hidden_by_default()
    {
        var button = Table.Descendants(Vsct + "Button").Single(b => (string)b.Attribute("id")! == "GoToDamlDefinitionCommand");
        var id = (string)Table.Descendants(Vsct + "IDSymbol").Single(s => (string)s.Attribute("name")! == "GoToDamlDefinitionCommand").Attribute("value")!;

        int.Parse(id.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture).ShouldBe(DamlMenuIds.GoToDamlDefinition);
        ((string)button.Element(Vsct + "Parent")!.Attribute("guid")!, (string)button.Element(Vsct + "Parent")!.Attribute("id")!)
            .ShouldBe(("VSMainMenu", "CodeWindow.NavigationLocationGroup"));
        button.Elements(Vsct + "CommandFlag").Select(f => f.Value).ShouldBe(["DynamicVisibility", "DefaultInvisible"], ignoreOrder: true);
        Table.Descendants(Vsct + "KeyBinding").ShouldNotContain(k => (string)k.Attribute("id")! == "GoToDamlDefinitionCommand");
    }
}
