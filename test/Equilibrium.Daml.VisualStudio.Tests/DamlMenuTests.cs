using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Tests;

// The menu lives in two places, the command table and the command classes; these keep them in step.
public class DamlMenuTests
{
    private static readonly XNamespace Vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
    private static readonly XDocument Table = XDocument.Load(Path.Combine(Repository.Root, "src", "Equilibrium.Daml.VisualStudio", "DamlPackage.vsct"));

    private static Guid GuidOf(string symbol) =>
        Guid.Parse((string)Table.Descendants(Vsct + "GuidSymbol").Single(g => (string)g.Attribute("name")! == symbol).Attribute("value")!);

    private static int IdOf(string symbol) =>
        int.Parse(((string)Table.Descendants(Vsct + "IDSymbol").Single(s => (string)s.Attribute("name")! == symbol).Attribute("value")!).Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    [Fact, Trait("Category", "Editor")]
    public void Every_command_class_has_a_button_in_the_daml_menu()
    {
        var commands = typeof(DamlPackage).Assembly.GetTypes()
            .Select(t => (Type: t, Command: t.GetCustomAttribute<CommandAttribute>()))
            .Where(c => c.Command is not null)
            .ToList();
        var menuButtons = Table.Descendants(Vsct + "Button")
            .Where(b => ((string)b.Element(Vsct + "Parent")!.Attribute("id")!).StartsWith("DamlMenu", StringComparison.Ordinal))
            .Select(b => (string)b.Attribute("id")!)
            .ToList();

        commands.Select(c => c.Type.Name).ShouldBe(menuButtons, ignoreOrder: true);
        foreach (var (type, command) in commands)
        {
            command!.Guid.ShouldBe(GuidOf("guidDamlCommandSet"), type.Name);
            command.Id.ShouldBe(IdOf(type.Name), type.Name);
        }
    }

    [Fact, Trait("Category", "Editor")]
    public void The_menu_sits_under_extensions_and_shows_only_with_daml_content()
    {
        var menu = Table.Descendants(Vsct + "Menu").Single(m => (string)m.Attribute("id")! == "DamlMenu");
        var parent = menu.Element(Vsct + "Parent")!;
        ((string)parent.Attribute("guid")!, (string)parent.Attribute("id")!).ShouldBe(("VSMainMenu", "Extensions.DefaultGroup"));
        menu.Elements(Vsct + "CommandFlag").Select(f => f.Value).ShouldContain("DefaultInvisible");

        var visibility = Table.Descendants(Vsct + "VisibilityItem").Single(v => (string)v.Attribute("id")! == "DamlMenu");
        GuidOf((string)visibility.Attribute("context")!).ShouldBe(new Guid(PackageGuids.DamlActiveString));
    }
}
