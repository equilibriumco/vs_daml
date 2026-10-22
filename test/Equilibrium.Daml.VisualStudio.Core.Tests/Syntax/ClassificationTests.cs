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

    [Fact, Trait("Category", "Unit")]
    public void Every_token_kind_has_a_decision()
    {
        // A new kind added to the lexer must be mapped (or left uncoloured) on purpose.
        // net48 has no generic GetValues.
#pragma warning disable CA2263
        foreach (DamlTokenKind kind in Enum.GetValues(typeof(DamlTokenKind)))
#pragma warning restore CA2263
        {
            Should.NotThrow(() => ClassificationMapper.Map(new DamlToken(0, 1, kind), "x".AsSpan()), kind.ToString());
        }
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("DA.List.sort", DamlClassificationNames.Identifier)]
    [InlineData("DA.Map.Map", DamlClassificationNames.Type)]
    [InlineData("M.T", DamlClassificationNames.Type)]
    [InlineData("DA.Map.!", DamlClassificationNames.Operator)]
    public void A_qualified_name_takes_the_colour_of_its_last_segment(string name, string expected) =>
        ClassificationMapper.Map(new DamlToken(0, name.Length, DamlTokenKind.QualifiedName), name.AsSpan()).ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void The_provider_classifies_a_line_with_the_state_of_the_lines_above()
    {
        var provider = new LexerSyntaxProvider(new TextLines("{- start", "template T", "-} template T"));

        provider.GetClassifications(1).ShouldBe([new ClassifiedSpan(0, 10, DamlClassificationNames.Comment)]);
        provider.GetClassifications(2).ShouldBe(
        [
            new ClassifiedSpan(0, 2, DamlClassificationNames.Comment),
            new ClassifiedSpan(3, 8, DamlClassificationNames.Declaration),
            new ClassifiedSpan(12, 1, DamlClassificationNames.Type),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Editing_through_the_provider_reports_the_lines_to_recolour()
    {
        var text = new TextLines("a = 1", "b = 2", "c = 3", "d = 4");
        var provider = new LexerSyntaxProvider(text);
        provider.GetClassifications(3);

        var opened = provider.ApplyEdit(text, text.Replace(1, 1, "b = {- 2"));
        var closed = provider.ApplyEdit(text, text.Replace(2, 1, "-} c = 3"));

        opened.ShouldBe(new LineRange(1, 3));
        closed.ShouldBe(new LineRange(2, 3));
        provider.GetClassifications(3).Select(s => s.Classification).ShouldBe([DamlClassificationNames.Identifier, DamlClassificationNames.Operator, DamlClassificationNames.Number]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Resetting_the_provider_starts_over_on_new_text()
    {
        var provider = new LexerSyntaxProvider(new TextLines("{- a", "b"));
        provider.GetClassifications(1).Single().Classification.ShouldBe(DamlClassificationNames.Comment);

        provider.Reset(new TextLines("a", "b"));

        provider.GetClassifications(1).Single().Classification.ShouldBe(DamlClassificationNames.Identifier);
    }

    [Fact, Trait("Category", "Unit")]
    public Task Classified_spans_of_a_fixture()
    {
        var text = TextLines.Parse(File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "lexer", "keywords.daml"), Encoding.UTF8));
        var provider = new LexerSyntaxProvider(text);
        var output = new StringBuilder();
        for (var line = 0; line < text.LineCount; line++)
        {
            var lineText = text.GetLineText(line);
            foreach (var span in provider.GetClassifications(line))
            {
                output.Append('L').Append(line + 1).Append(" [").Append(span.Start).Append("..").Append(span.Start + span.Length).Append(") ")
                    .Append(span.Classification).Append(" \"").Append(lineText, span.Start, span.Length).Append('"').Append('\n');
            }
        }
        return Verifier.Verify(output.ToString(), extension: "txt");
    }
}
