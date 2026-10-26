using System;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.ScriptResults;

public class RevealRequestTests
{
    // As damlc 3.6 writes it into the results page: the arguments are a URL-encoded JSON array.
    private const string Recorded = "command:daml.revealLocation?%5B%22file%3A%2F%2FC%3A%2Fwork%2Fsingle-package%2Fdaml%2FMain.daml%22%2C%2033%2C%2037%5D";

    private static string Command(string json) => "command:daml.revealLocation?" + Uri.EscapeDataString(json);

    private static string Refusal(string command)
    {
        RevealRequest.TryParse(command, out var request, out var rejection).ShouldBeFalse();
        request.ShouldBeNull();
        return rejection.ShouldNotBeNull();
    }

    private static int LengthOf(int line) => 10 + line;

    // A one-line range selects that whole line; a longer one ends at the start of its end line,
    // or at the end of the document when that is where it ends.
    [Theory, Trait("Category", "Unit")]
    [InlineData(4, 4, 4, 14)]
    [InlineData(2, 5, 5, 0)]
    [InlineData(7, 10, 9, 19)]
    public void The_selection_ends_where_the_range_says(int start, int end, int endLine, int endColumn)
    {
        new RevealRequest(@"C:\work\a.daml", start, end).SelectionEnd(10, LengthOf).ShouldBe((endLine, endColumn));
    }

    [Fact, Trait("Category", "Unit")]
    public void A_recorded_link_names_the_file_and_its_lines()
    {
        RevealRequest.TryParse(Recorded, out var request, out var rejection).ShouldBeTrue(rejection);

        request.ShouldBe(new RevealRequest(@"C:\work\single-package\daml\Main.daml", 33, 37));
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("file:///C:/work/daml/Main.daml", @"C:\work\daml\Main.daml")]
    [InlineData("file://C:/work/daml/Main.daml", @"C:\work\daml\Main.daml")]
    [InlineData("file:///c%3A/work/my%20app/daml/Main.daml", @"c:\work\my app\daml\Main.daml")]
    [InlineData("FILE:///C:/work/daml/Lib.DAML", @"C:\work\daml\Lib.DAML")]
    [InlineData("file://server/share/daml/Main.daml", @"\\server\share\daml\Main.daml")]
    public void File_urls_in_the_forms_damlc_and_others_write_are_read(string url, string path)
    {
        RevealRequest.TryParse(Command($"[\"{url}\", 1, 2]"), out var request, out var rejection).ShouldBeTrue(rejection);

        request!.File.ShouldBe(path);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("""["file:///C:/work/daml/../../Windows/win.ini", 0, 0]""", "a path that climbs out with ..")]
    [InlineData("""["file:///C:/work/daml/./Main.daml", 0, 0]""", "a dot segment")]
    [InlineData("""["file:///C:/work/daml/%2E%2E/x/Main.daml", 0, 0]""", "an encoded .. segment")]
    [InlineData("""["file:///C:/work/daml/Main.cs", 0, 0]""", "a file that isn't Daml")]
    [InlineData("""["file:///C:/work/daml/Main.daml:stream", 0, 0]""", "an alternate data stream")]
    [InlineData("""["file:////?/C:/work/Main.daml", 0, 0]""", "a device path")]
    [InlineData("""["file:///C:/work/Main.daml?x=1", 0, 0]""", "a query")]
    [InlineData("""["file:///C:/work/Main.daml#top", 0, 0]""", "a fragment")]
    [InlineData("""["https://example.com/Main.daml", 0, 0]""", "a web address")]
    [InlineData("""["daml://compiler?file=x&top-level-decl=s", 0, 0]""", "a virtual resource")]
    [InlineData("""["file:Main.daml", 0, 0]""", "a relative path")]
    [InlineData("""["file:///C:/work/%ZZ.daml", 0, 0]""", "a broken escape")]
    [InlineData("""["file:///C:/work/Main.daml", -1, 2]""", "a negative line")]
    [InlineData("""["file:///C:/work/Main.daml", 5, 2]""", "an end before the start")]
    [InlineData("""["file:///C:/work/Main.daml", 0, 2000000]""", "an absurd line")]
    [InlineData("""["file:///C:/work/Main.daml", 18446744073709551616, 18446744073709551616]""", "a start beyond Int64")]
    [InlineData("""["file:///C:/work/Main.daml", 0, 18446744073709551616]""", "an end beyond Int64")]
    [InlineData("""["file:///C:/work/Main.daml", 0, 1.5]""", "a fractional line")]
    [InlineData("""["file:///C:/work/Main.daml", "0", 2]""", "a line given as text")]
    [InlineData("""["file:///C:/work/Main.daml", 0]""", "a missing end")]
    [InlineData("""["file:///C:/work/Main.daml", 0, 2, "run"]""", "an extra argument")]
    [InlineData("""{"uri":"file:///C:/work/Main.daml"}""", "an object instead of the list")]
    [InlineData("not json", "something that isn't JSON")]
    public void Anything_but_a_daml_source_and_a_sane_range_is_refused(string json, string what)
    {
        Refusal(Command(json)).ShouldNotBeNullOrEmpty(what);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("command:workbench.action.terminal.new")]
    [InlineData("command:daml.revealLocationX?%5B%5D")]
    [InlineData("command:daml.revealLocation")]
    [InlineData("command:daml.revealLocation?%5B%22file%3A%2F%2FC%3A%2FMain.daml%22%2C1%2C2%5D%ZZ")]
    [InlineData("javascript:alert(1)")]
    public void Other_commands_and_broken_links_are_refused(string command)
    {
        Refusal(command).ShouldNotBeNullOrEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void An_oversized_link_is_refused_before_it_is_decoded()
    {
        Refusal(Command($"[\"file:///C:/{new string('a', 5000)}.daml\", 0, 0]")).ShouldContain("long");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(0, 0, 1, true)]
    [InlineData(3, 7, 10, true)]
    [InlineData(3, 10, 10, true)]
    [InlineData(10, 10, 10, false)]
    [InlineData(3, 11, 10, false)]
    [InlineData(0, 0, 0, false)]
    public void A_range_fits_a_document_only_when_its_lines_are_there(int start, int end, int lineCount, bool fits)
    {
        new RevealRequest(@"C:\work\Main.daml", start, end).FitsIn(lineCount).ShouldBe(fits);
    }
}
