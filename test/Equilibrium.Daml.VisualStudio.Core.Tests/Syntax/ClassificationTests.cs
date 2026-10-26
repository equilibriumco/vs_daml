using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Shouldly;
using VerifyXunit;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Syntax;

public class ClassificationTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlTokenKind.Keyword, DamlClassificationNames.Keyword)]
    [InlineData(DamlTokenKind.ControlKeyword, DamlClassificationNames.Keyword)]
    [InlineData(DamlTokenKind.ModuleKeyword, DamlClassificationNames.Keyword)]
    [InlineData(DamlTokenKind.ImportKeyword, DamlClassificationNames.Keyword)]
    [InlineData(DamlTokenKind.Boolean, DamlClassificationNames.Keyword)]
    [InlineData(DamlTokenKind.DeclarationKeyword, DamlClassificationNames.Declaration)]
    [InlineData(DamlTokenKind.ContractKeyword, DamlClassificationNames.ContractKeyword)]
    [InlineData(DamlTokenKind.ChoiceModifier, DamlClassificationNames.ContractKeyword)]
    [InlineData(DamlTokenKind.ConId, DamlClassificationNames.Type)]
    [InlineData(DamlTokenKind.VarId, DamlClassificationNames.Identifier)]
    [InlineData(DamlTokenKind.LineComment, DamlClassificationNames.Comment)]
    [InlineData(DamlTokenKind.BlockComment, DamlClassificationNames.Comment)]
    [InlineData(DamlTokenKind.DocComment, DamlClassificationNames.DocComment)]
    [InlineData(DamlTokenKind.Pragma, DamlClassificationNames.Pragma)]
    [InlineData(DamlTokenKind.String, DamlClassificationNames.StringLiteral)]
    [InlineData(DamlTokenKind.Char, DamlClassificationNames.StringLiteral)]
    [InlineData(DamlTokenKind.Number, DamlClassificationNames.Number)]
    [InlineData(DamlTokenKind.Operator, DamlClassificationNames.Operator)]
    [InlineData(DamlTokenKind.ReservedOp, DamlClassificationNames.Operator)]
    [InlineData(DamlTokenKind.Backtick, DamlClassificationNames.Operator)]
    [InlineData(DamlTokenKind.TypeApplication, DamlClassificationNames.Operator)]
    public void Token_kinds_map_to_daml_classifications(DamlTokenKind kind, string expected) =>
        ClassificationMapper.Map(new DamlToken(0, 1, kind), "x".AsSpan()).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlTokenKind.Whitespace)]
    [InlineData(DamlTokenKind.Punctuation)]
    [InlineData(DamlTokenKind.Unit)]
    [InlineData(DamlTokenKind.Error)]
    public void Some_kinds_stay_uncoloured(DamlTokenKind kind) =>
        ClassificationMapper.Map(new DamlToken(0, 1, kind), "x".AsSpan()).ShouldBeNull();

    [Theory, Trait("Category", "Unit")]
    [InlineData("DA.List.sort", DamlClassificationNames.Identifier)]
    [InlineData("DA.Map.Map", DamlClassificationNames.Type)]
    [InlineData("M.T", DamlClassificationNames.Type)]
    [InlineData("DA.Map.!", DamlClassificationNames.Operator)]
    public void A_qualified_name_takes_the_colour_of_its_last_segment(string name, string expected) =>
        ClassificationMapper.Map(new DamlToken(0, name.Length, DamlTokenKind.QualifiedName), name.AsSpan()).ShouldBe(expected);

    // The spans come from the lexed line, so a line inside a block comment opened above is coloured
    // as such; whitespace, brackets and errors stay in the default colour and are left out.
    [Fact, Trait("Category", "Unit")]
    public void The_spans_of_a_line_follow_the_state_of_the_lines_above()
    {
        var lines = LineTokens.Lex(["{- start", "template T", "-} template T"]);

        ClassificationMapper.SpansOf(lines[1]).ShouldBe([new ClassifiedSpan(0, 10, DamlClassificationNames.Comment)]);
        ClassificationMapper.SpansOf(lines[2]).ShouldBe(
        [
            new ClassifiedSpan(0, 2, DamlClassificationNames.Comment),
            new ClassifiedSpan(3, 8, DamlClassificationNames.Declaration),
            new ClassifiedSpan(12, 1, DamlClassificationNames.Type),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public Task Classified_spans_of_a_fixture()
    {
        var lines = LineTokens.Lex(File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "lexer", "keywords.daml"), Encoding.UTF8));
        var output = new StringBuilder();
        for (var line = 0; line < lines.Count; line++)
        {
            foreach (var span in ClassificationMapper.SpansOf(lines[line]))
            {
                output.Append('L').Append(line + 1).Append(" [").Append(span.Start).Append("..").Append(span.Start + span.Length).Append(") ")
                    .Append(span.Classification).Append(" \"").Append(lines[line].Text, span.Start, span.Length).Append('"').Append('\n');
            }
        }
        return Verifier.Verify(output.ToString(), extension: "txt");
    }
}
