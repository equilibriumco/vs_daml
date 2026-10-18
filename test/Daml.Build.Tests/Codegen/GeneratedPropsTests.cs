using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Daml.Build.Core.Codegen;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public sealed class GeneratedPropsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-props-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Props => Path.Combine(_root, @"obj\daml\App.csproj.Debug.net10.0.g.props");

    private const string Generation = @"C:\work\App\obj\Debug\net10.0\Daml\Contracts-0123456789abcdef\Kinds\Asset\";

    // Any change to the Daml gives the package a new ID, and the generator names its registration after it.
    [Fact, Trait("Category", "Unit")]
    public void A_registration_renamed_for_the_new_package_is_replaced_not_removed()
    {
        var before = new[] { Generation + "Asset.cs", Generation + "PackageRegistration_" + new string('a', 64) + ".cs" };
        var after = new[] { Generation + "Asset.cs", Generation + "PackageRegistration_" + new string('b', 64) + ".cs" };

        GeneratedProps.Removed(before, after).ShouldBe(0);
    }

    [Fact, Trait("Category", "Unit")]
    public void Files_the_new_list_lacks_are_removed()
    {
        var before = new[] { Generation + "Asset.cs", Generation + "Asset.Give.cs", Generation + "PackageRegistration_" + new string('a', 64) + ".cs" };
        var after = new[] { Generation.ToUpperInvariant() + "ASSET.CS", Generation + "PackageRegistration_" + new string('b', 64) + ".cs" };

        GeneratedProps.Removed(before, after).ShouldBe(1);
    }

    private static readonly GeneratedFile[] Files =
    [
        new(@"C:\work\App\obj\Debug\net10.0\Daml\Contracts-0123456789abcdef\Asset.cs", "0123456789abcdef"),
        new(@"C:\work\App\obj\Debug\net10.0\Daml\Contracts-0123456789abcdef\Asset.Give.cs", "0123456789abcdef"),
    ];

    [Fact, Trait("Category", "Unit")]
    public void The_props_list_each_file_as_generated_compile_items_hidden_under_obj()
    {
        GeneratedProps.Write(Props, Files, visible: false).ShouldBeTrue();

        var items = XDocument.Load(Props).Root!.Elements("ItemGroup").Elements("Compile").ToList();
        items.Select(i => (string?)i.Attribute("Include")).ShouldBe(Files.Select(f => f.Path));
        items.ShouldAllBe(i => (string?)i.Attribute("DamlGenerated") == "true"
            && (string?)i.Attribute("DamlGenerationId") == "0123456789abcdef"
            && (string?)i.Attribute("Visible") == "false");
    }

    // An unchanged file must not make Visual Studio evaluate the project again.
    [Fact, Trait("Category", "Unit")]
    public void The_same_list_again_leaves_the_file_alone()
    {
        GeneratedProps.Write(Props, Files, visible: false);
        var written = File.GetLastWriteTimeUtc(Props);

        GeneratedProps.Write(Props, Files, visible: false).ShouldBeFalse();
        GeneratedProps.Write(Props, Files.Take(1), visible: false).ShouldBeTrue();

        File.GetLastWriteTimeUtc(Props).ShouldBeGreaterThanOrEqualTo(written);
        XDocument.Load(Props).Root!.Descendants("Compile").ShouldHaveSingleItem();
    }

    // Source mode keeps generated code in the project, where it is shown.
    [Fact, Trait("Category", "Unit")]
    public void Visible_files_carry_no_Visible_metadata()
    {
        GeneratedProps.Write(Props, Files, visible: true);

        XDocument.Load(Props).Root!.Descendants("Compile").ShouldAllBe(i => i.Attribute("Visible") == null);
    }

    // MSBuild would read these as properties, items, metadata or wildcards.
    [Fact, Trait("Category", "Unit")]
    public void Characters_MSBuild_or_XML_treat_specially_are_escaped()
    {
        GeneratedProps.Write(Props, [new GeneratedFile(@"C:\w&k\$(X)\@(Y)\%(Z);a*b?'c<d>.cs", "id")], visible: true);

        var text = File.ReadAllText(Props);
        text.ShouldContain(@"C:\w&amp;k\%24(X)\%40(Y)\%25(Z)%3Ba%2Ab%3F%27c&lt;d&gt;.cs");
        XDocument.Load(Props).Root!.Descendants("Compile").ShouldHaveSingleItem();
    }

    [Fact, Trait("Category", "Unit")]
    public void An_empty_props_file_is_made_once_and_never_overwrites_a_list()
    {
        GeneratedProps.EnsureExists(Props).ShouldBeTrue();
        XDocument.Load(Props).Root!.Name.LocalName.ShouldBe("Project");

        GeneratedProps.Write(Props, Files, visible: false);
        GeneratedProps.EnsureExists(Props).ShouldBeFalse();

        XDocument.Load(Props).Root!.Descendants("Compile").Count().ShouldBe(2);
    }

    // What background sync reads back: the files as written, whatever characters they hold.
    [Fact, Trait("Category", "Unit")]
    public void A_list_reads_back_as_the_files_written()
    {
        GeneratedFile[] files = [.. Files, new(@"C:\work\App\obj\Debug\net10.0\Daml\Odd;$@%'-0123456789abcdef\A & B.cs", "0123456789abcdef")];
        GeneratedProps.Write(Props, files, visible: true);

        GeneratedProps.Read(Props).ShouldBe(files);
        GeneratedProps.Read(Path.Combine(_root, "missing.g.props")).ShouldBeEmpty();
    }
}
