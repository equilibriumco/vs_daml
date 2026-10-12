using Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

public class DamlWordsTests
{
    // The caret is where '|' stands; the expected word is between '[' and ']'.
    [Theory, Trait("Category", "Unit")]
    [InlineData("  [fol|dl'] f xs", true)]
    [InlineData("  [foldl|'] f xs", true)]
    [InlineData("let [x|''] = 1", true)]
    [InlineData("[create_|iou] with", true)]
    [InlineData("DA.List.[fol|dl'] x", true)]
    [InlineData("[D|A].List.foldl' x", true)]
    [InlineData("total = [4|2]", true)]
    [InlineData("x [<|-] create", true)]
    [InlineData("a [=|=] b", true)]
    [InlineData("f[|(]x)", true)]
    [InlineData("x[  |  ]y", false)]
    [InlineData("[|']a' ", true)]
    [InlineData("[x|'] ", true)]
    [InlineData("[Üb|er] 1", true)]
    public void The_word_under_the_caret_is_found(string marked, bool significant)
    {
        var caret = marked.Replace("[", "").Replace("]", "").IndexOf('|');
        var line = marked.Replace("[", "").Replace("]", "").Replace("|", "");
        var start = marked.Replace("|", "").IndexOf('[');
        var end = marked.Replace("|", "").IndexOf(']') - 1;

        var word = DamlWords.WordAt(line, caret);

        line.Substring(word.Start, word.Length).ShouldBe(line.Substring(start, end - start));
        word.IsSignificant.ShouldBe(significant);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("abc")]
    public void At_the_end_of_a_line_the_word_is_empty(string line)
    {
        var word = DamlWords.WordAt(line, line.Length);

        word.Start.ShouldBe(line.Length);
        word.Length.ShouldBe(0);
        word.IsSignificant.ShouldBeFalse();
    }
}
