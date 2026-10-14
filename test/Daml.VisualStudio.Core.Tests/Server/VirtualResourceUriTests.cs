using System;
using Daml.VisualStudio.Core.Server;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Server;

public class VirtualResourceUriTests
{
    // As damlc 3.6 sends it in a code lens for samples/single-package.
    private const string Recorded = "daml://compiler?file=M%3A%2FMoje%2FProjekty%2Fvs_daml%2Fsamples%2Fsingle-package%2Fdaml%2FMain.daml&top-level-decl=setup";

    [Fact, Trait("Category", "Unit")]
    public void A_recorded_uri_names_the_file_and_the_declaration()
    {
        var uri = VirtualResourceUri.Parse(Recorded);

        uri.File.ShouldBe(@"C:\work\vs_daml\samples\single-package\daml\Main.daml");
        uri.TopLevelDecl.ShouldBe("setup");
    }

    [Fact, Trait("Category", "Unit")]
    public void Formatting_gives_back_what_damlc_sent()
    {
        VirtualResourceUri.Parse(Recorded).ToString().ShouldBe(Recorded);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\my app\daml\Main.daml", "setup")]
    [InlineData(@"C:\work\zażółć 日本\daml\Main.daml", "test'")]
    [InlineData(@"C:\a&b=c\daml\Main.daml", "run_all")]
    [InlineData(@"C:\100%\x+y#z\Main.daml", "s")]
    public void Unusual_paths_and_names_survive_a_round_trip(string file, string declaration)
    {
        var uri = VirtualResourceUri.For(file, declaration);

        var parsed = VirtualResourceUri.Parse(uri.ToString());

        parsed.File.ShouldBe(file);
        parsed.TopLevelDecl.ShouldBe(declaration);
        parsed.ShouldBe(uri);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_same_file_in_another_case_or_with_forward_slashes_is_the_same_resource()
    {
        var a = VirtualResourceUri.Parse("daml://compiler?file=C%3A%2Fwork%2Fdaml%2FMain.daml&top-level-decl=setup");
        var b = VirtualResourceUri.For(@"c:\WORK\daml\main.daml", "setup");

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(VirtualResourceUri.For(@"C:\work\daml\Main.daml", "Setup"), "declarations are case-sensitive");
    }

    [Fact, Trait("Category", "Unit")]
    public void Parameters_in_another_order_or_extra_ones_are_accepted()
    {
        var uri = VirtualResourceUri.Parse("DAML://compiler?top-level-decl=setup&x=1&file=C%3A%5Cwork%5CMain.daml");

        uri.File.ShouldBe(@"C:\work\Main.daml");
        uri.TopLevelDecl.ShouldBe("setup");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/work/Main.daml")]
    [InlineData("daml://other?file=C%3A%2FMain.daml&top-level-decl=s")]
    [InlineData("daml://compiler?file=C%3A%2FMain.daml")]
    [InlineData("daml://compiler?top-level-decl=setup")]
    [InlineData("daml://compiler?file=relative%2FMain.daml&top-level-decl=setup")]
    [InlineData("daml://compiler?file=C%3A%2FMain.daml&top-level-decl=")]
    [InlineData("daml://compiler?file=%ZZ&top-level-decl=s")]
    public void Anything_else_is_not_a_virtual_resource(string? text)
    {
        VirtualResourceUri.TryParse(text, out var uri).ShouldBeFalse();
        uri.ShouldBeNull();
        if (text is not null)
        {
            Should.Throw<FormatException>(() => VirtualResourceUri.Parse(text));
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void The_address_comparer_treats_two_spellings_of_one_resource_as_one_and_other_text_ordinally()
    {
        var comparer = VirtualResourceAddressComparer.Instance;
        const string OneSpelling = "daml://compiler?file=C%3A%2Fwork%2FMain.daml&top-level-decl=setup";
        const string OtherSpelling = "daml://compiler?top-level-decl=setup&file=c%3A%5CWORK%5Cmain.daml";

        comparer.Equals(OneSpelling, OtherSpelling).ShouldBeTrue();
        comparer.GetHashCode(OneSpelling).ShouldBe(comparer.GetHashCode(OtherSpelling));
        comparer.Equals(OneSpelling, OneSpelling.Replace("setup", "other")).ShouldBeFalse();
        comparer.Equals("x", "x").ShouldBeTrue();
        comparer.Equals("x", "X").ShouldBeFalse();
        comparer.Equals(OneSpelling, "x").ShouldBeFalse();
    }
}
