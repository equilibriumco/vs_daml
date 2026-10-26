using System.Collections.Generic;
using System.Linq;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;
using static Equilibrium.Daml.VisualStudio.Core.Syntax.DamlTokenKind;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Syntax;

public class DamlLexerTests
{
    // The state a line ends in when nothing is left open.
    private static readonly LexState Normal = new(LexMode.Normal, 0);

    internal static (List<(string Text, DamlTokenKind Kind)> Tokens, LexState End) Lex(string line, LexState start = default)
    {
        var tokens = new List<DamlToken>();
        var end = DamlLexer.LexLine(System.MemoryExtensions.AsSpan(line), start, tokens);
        return (tokens.Select(t => (line.Substring(t.Start, t.Length), t.Kind)).ToList(), end);
    }

    // Tokens with the whitespace left out, which is what most rules care about.
    private static (string Text, DamlTokenKind Kind)[] Significant(string line, LexState start = default) =>
        Lex(line, start).Tokens.Where(t => t.Kind != Whitespace).ToArray();

    private static DamlTokenKind Single(string text) => Significant(text).ShouldHaveSingleItem().Kind;

    [Theory, Trait("Category", "Unit")]
    [InlineData("module", ModuleKeyword)]
    [InlineData("import", ImportKeyword)]
    [InlineData("qualified", ImportKeyword)]
    [InlineData("as", ImportKeyword)]
    [InlineData("hiding", ImportKeyword)]
    [InlineData("where", Keyword)]
    [InlineData("template", DeclarationKeyword)]
    [InlineData("interface", DeclarationKeyword)]
    [InlineData("exception", DeclarationKeyword)]
    [InlineData("data", DeclarationKeyword)]
    [InlineData("type", DeclarationKeyword)]
    [InlineData("newtype", DeclarationKeyword)]
    [InlineData("class", DeclarationKeyword)]
    [InlineData("instance", DeclarationKeyword)]
    [InlineData("deriving", DeclarationKeyword)]
    [InlineData("let", DeclarationKeyword)]
    [InlineData("in", DeclarationKeyword)]
    [InlineData("with", ContractKeyword)]
    [InlineData("choice", ContractKeyword)]
    [InlineData("controller", ContractKeyword)]
    [InlineData("signatory", ContractKeyword)]
    [InlineData("observer", ContractKeyword)]
    [InlineData("ensure", ContractKeyword)]
    [InlineData("key", ContractKeyword)]
    [InlineData("maintainer", ContractKeyword)]
    [InlineData("agreement", ContractKeyword)]
    [InlineData("message", ContractKeyword)]
    [InlineData("viewtype", ContractKeyword)]
    [InlineData("requires", ContractKeyword)]
    [InlineData("implements", ContractKeyword)]
    [InlineData("for", ContractKeyword)]
    [InlineData("nonconsuming", ChoiceModifier)]
    [InlineData("preconsuming", ChoiceModifier)]
    [InlineData("postconsuming", ChoiceModifier)]
    [InlineData("do", ControlKeyword)]
    [InlineData("if", ControlKeyword)]
    [InlineData("then", ControlKeyword)]
    [InlineData("else", ControlKeyword)]
    [InlineData("case", ControlKeyword)]
    [InlineData("of", ControlKeyword)]
    [InlineData("try", ControlKeyword)]
    [InlineData("catch", ControlKeyword)]
    [InlineData("True", Boolean)]
    [InlineData("False", Boolean)]
    public void Keywords_get_their_own_kind(string word, DamlTokenKind kind) => Single(word).ShouldBe(kind);

    [Theory, Trait("Category", "Unit")]
    [InlineData("this", VarId)]
    [InlineData("self", VarId)]
    [InlineData("arg", VarId)]
    [InlineData("owner", VarId)]
    [InlineData("_unused", VarId)]
    [InlineData("foldl'", VarId)]
    [InlineData("x''", VarId)]
    [InlineData("doThing", VarId)]
    [InlineData("letters2", VarId)]
    [InlineData("zażółć", VarId)]
    [InlineData("Asset", ConId)]
    [InlineData("Óscar", ConId)]
    [InlineData("Party'", ConId)]
    [InlineData("()", Unit)]
    public void Identifiers_are_varids_or_conids(string text, DamlTokenKind kind) => Single(text).ShouldBe(kind);

    // damlc accepts subscripts, superscripts and combining accents after the first character of a
    // name, as in daml-finance's "fra t₁ t₂ r₀ r₁".
    [Theory, Trait("Category", "Unit")]
    [InlineData("t₁", VarId)]
    [InlineData("r₀", VarId)]
    [InlineData("x²", VarId)]
    [InlineData("y₁'", VarId)]
    [InlineData("P₁", ConId)]
    [InlineData("café", VarId)]
    [InlineData("x\U00010107", VarId)]
    public void Subscripts_superscripts_and_combining_marks_continue_a_name(string text, DamlTokenKind kind) => Single(text).ShouldBe(kind);

    [Fact, Trait("Category", "Unit")]
    public void Roman_numerals_and_a_leading_subscript_do_not_belong_to_a_name()
    {
        // damlc rejects both: letter numbers aren't name characters, and no digit starts a name.
        Significant("vⅫ")[0].ShouldBe(("v", VarId));
        Significant("₁x")[0].Kind.ShouldBe(Error);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("DA.List.sort")]
    [InlineData("M.T")]
    [InlineData("DA.Map.Map")]
    [InlineData("Map.!")]
    [InlineData("P.<$>")]
    public void Qualified_names_are_one_token(string text) => Single(text).ShouldBe(QualifiedName);

    [Fact, Trait("Category", "Unit")]
    public void A_spaced_dot_is_composition_and_a_lowercase_dot_is_field_access()
    {
        Significant("Foo . bar").ShouldBe([("Foo", ConId), (".", Operator), ("bar", VarId)]);
        Significant("this.owner").ShouldBe([("this", VarId), (".", Operator), ("owner", VarId)]);
        Significant("Some.").ShouldBe([("Some", ConId), (".", Operator)]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("-- a comment", LineComment)]
    [InlineData("--", LineComment)]
    [InlineData("------ banner ------", LineComment)]
    [InlineData("-- | documents the next thing", DocComment)]
    [InlineData("-- ^ documents the previous thing", DocComment)]
    [InlineData("--   |  wide", DocComment)]
    [InlineData("-->", Operator)]
    [InlineData("--|", Operator)]
    [InlineData("|--", Operator)]
    [InlineData("<--", Operator)]
    public void Dashes_start_a_comment_only_when_they_are_not_part_of_an_operator(string text, DamlTokenKind kind) =>
        Lex(text).Tokens[0].Kind.ShouldBe(kind);

    [Fact, Trait("Category", "Unit")]
    public void A_line_comment_runs_to_the_end_of_the_line()
    {
        Significant("x = 1 -- one {- not a block").ShouldBe(
            [("x", VarId), ("=", ReservedOp), ("1", Number), ("-- one {- not a block", LineComment)]);
        Lex("x --> y").Tokens.Select(t => t.Kind).ShouldBe([VarId, Whitespace, Operator, Whitespace, VarId]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Block_comments_nest()
    {
        var (tokens, end) = Lex("{- a {- b -} c -} x");

        tokens.ShouldBe([("{- a {- b -} c -}", BlockComment), (" ", Whitespace), ("x", VarId)]);
        end.ShouldBe(Normal);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_open_block_comment_carries_its_depth_to_the_next_lines()
    {
        var first = Lex("x {- outer {- inner -}");
        first.Tokens[^1].ShouldBe(("{- outer {- inner -}", BlockComment));
        first.End.ShouldBe(new LexState(LexMode.BlockComment, 1));

        var second = Lex("  still {- deeper", first.End);
        second.Tokens.ShouldBe([("  still {- deeper", BlockComment)]);
        second.End.ShouldBe(new LexState(LexMode.BlockComment, 2));

        var third = Lex("-} -} module", second.End);
        third.Tokens.ShouldBe([("-} -}", BlockComment), (" ", Whitespace), ("module", ModuleKeyword)]);
        third.End.ShouldBe(Normal);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_pragma_can_span_three_lines()
    {
        var first = Lex("{-# LANGUAGE");
        first.Tokens.ShouldBe([("{-# LANGUAGE", Pragma)]);
        first.End.ShouldBe(new LexState(LexMode.Pragma, 1));

        var second = Lex("    ApplicativeDo,", first.End);
        second.Tokens.ShouldBe([("    ApplicativeDo,", Pragma)]);
        second.End.ShouldBe(first.End);

        var third = Lex("    RecordWildCards #-} module M where", second.End);
        third.Tokens[0].ShouldBe(("    RecordWildCards #-}", Pragma));
        third.Tokens.Skip(1).Where(t => t.Kind != Whitespace).ShouldBe([("module", ModuleKeyword), ("M", ConId), ("where", Keyword)]);
        third.End.ShouldBe(Normal);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("\"plain\"")]
    [InlineData("\"with \\\"escaped\\\" quotes\"")]
    [InlineData("\"tab\\tand \\\\ backslash\"")]
    [InlineData("\"\"")]
    [InlineData("\"-- not a comment {- nor this\"")]
    [InlineData("\"gap closed \\   \\ on the same line\"")]
    public void Strings_are_one_token_including_escapes(string text) => Single(text).ShouldBe(String);

    [Fact, Trait("Category", "Unit")]
    public void A_string_left_open_without_a_gap_ends_at_the_end_of_the_line()
    {
        var (tokens, end) = Lex("s = \"unfinished");

        tokens[^1].ShouldBe(("\"unfinished", String));
        end.ShouldBe(Normal);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_string_gap_continues_the_string_on_later_lines()
    {
        var first = Lex("s = \"abc\\");
        first.Tokens[^1].ShouldBe(("\"abc\\", String));
        first.End.ShouldBe(new LexState(LexMode.StringGap, 0));

        var blank = Lex("   ", first.End);
        blank.Tokens.ShouldBe([("   ", String)]);
        blank.End.ShouldBe(first.End);

        var last = Lex("    \\def\" ++ x", blank.End);
        last.Tokens.Where(t => t.Kind != Whitespace).ShouldBe([("    \\def\"", String), ("++", Operator), ("x", VarId)]);
        last.End.ShouldBe(Normal);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_broken_string_gap_falls_back_to_normal_code()
    {
        var (tokens, end) = Lex("  x = 1", new LexState(LexMode.StringGap, 0));

        tokens.Where(t => t.Kind != Whitespace).ShouldBe([("x", VarId), ("=", ReservedOp), ("1", Number)]);
        end.ShouldBe(Normal);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("'a'")]
    [InlineData("'\\n'")]
    [InlineData("'\\''")]
    [InlineData("'\\\\'")]
    [InlineData("'\\x41'")]
    [InlineData("'\"'")]
    [InlineData("' '")]
    public void Char_literals_are_one_token(string text) => Single(text).ShouldBe(Char);

    [Fact, Trait("Category", "Unit")]
    public void An_apostrophe_after_an_identifier_belongs_to_it()
    {
        Significant("f x' 'a'").ShouldBe([("f", VarId), ("x'", VarId), ("'a'", Char)]);
        Significant("foldl' (+) 0").ShouldBe([("foldl'", VarId), ("(", Punctuation), ("+", Operator), (")", Punctuation), ("0", Number)]);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_promotion_tick_is_not_a_char_literal()
    {
        Significant("'[]").ShouldBe([("'", Punctuation), ("[", Punctuation), ("]", Punctuation)]);
        Significant("'True").ShouldBe([("'", Punctuation), ("True", Boolean)]);
        Significant("'(:)").ShouldBe([("'", Punctuation), ("(", Punctuation), (":", ReservedOp), (")", Punctuation)]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("1_000_000")]
    [InlineData("0x1F")]
    [InlineData("0XfF_ff")]
    [InlineData("0o17")]
    [InlineData("0b1010_0101")]
    [InlineData("3.14")]
    [InlineData("1e10")]
    [InlineData("2.5e-3")]
    [InlineData("6.02E+23")]
    [InlineData("1_0.0_1")]
    public void Numbers_cover_every_literal_form(string text) => Single(text).ShouldBe(Number);

    [Fact, Trait("Category", "Unit")]
    public void Partial_number_forms_split_at_the_first_character_that_does_not_fit()
    {
        Significant("1.").ShouldBe([("1", Number), (".", Operator)]);
        Significant("1.x").ShouldBe([("1", Number), (".", Operator), ("x", VarId)]);
        Significant("0x").ShouldBe([("0", Number), ("x", VarId)]);
        Significant("2e").ShouldBe([("2", Number), ("e", VarId)]);
        Significant("[1..5]").ShouldBe([("[", Punctuation), ("1", Number), ("..", ReservedOp), ("5", Number), ("]", Punctuation)]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("->")]
    [InlineData("<-")]
    [InlineData("::")]
    [InlineData(":")]
    [InlineData("=>")]
    [InlineData("=")]
    [InlineData("|")]
    [InlineData("\\")]
    [InlineData("..")]
    [InlineData("~")]
    public void Reserved_operators_are_told_apart(string text) => Single(text).ShouldBe(ReservedOp);

    [Theory, Trait("Category", "Unit")]
    [InlineData("++")]
    [InlineData("<$>")]
    [InlineData(">>=")]
    [InlineData("==")]
    [InlineData("&&")]
    [InlineData("-")]
    [InlineData("$")]
    [InlineData("<>")]
    [InlineData("∘")]
    public void Other_symbol_runs_are_operators(string text) => Single(text).ShouldBe(Operator);

    [Theory, Trait("Category", "Unit")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("{")]
    [InlineData("}")]
    public void Brackets_and_separators_are_punctuation(string text) => Single(text).ShouldBe(Punctuation);

    [Fact, Trait("Category", "Unit")]
    public void An_at_sign_before_a_type_is_a_type_application_and_after_a_name_an_as_pattern()
    {
        Significant("fromAnyChoice @Transfer x").ShouldBe([("fromAnyChoice", VarId), ("@", TypeApplication), ("Transfer", ConId), ("x", VarId)]);
        Significant("f (@Int)").ShouldBe([("f", VarId), ("(", Punctuation), ("@", TypeApplication), ("Int", ConId), (")", Punctuation)]);
        Significant("all@(x:_)").ShouldBe(
            [("all", VarId), ("@", ReservedOp), ("(", Punctuation), ("x", VarId), (":", ReservedOp), ("_", VarId), (")", Punctuation)]);
        Significant("a <@> b").ShouldBe([("a", VarId), ("<@>", Operator), ("b", VarId)]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Backticked_names_are_one_infix_token()
    {
        Significant("a `elem` b").ShouldBe([("a", VarId), ("`elem`", Backtick), ("b", VarId)]);
        Significant("a `DA.List.elem` b").ShouldBe([("a", VarId), ("`DA.List.elem`", Backtick), ("b", VarId)]);
        Significant("a ` b").ShouldBe([("a", VarId), ("`", Backtick), ("b", VarId)]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("\u0001")]
    [InlineData("\u00BF")]
    public void Characters_that_cannot_start_any_token_are_errors(string text) => Single(text).ShouldBe(Error);

    [Fact, Trait("Category", "Unit")]
    public void A_template_header_lexes_as_expected()
    {
        Significant("template Iou with issuer : Party; amount : Decimal where").ShouldBe(
        [
            ("template", DeclarationKeyword), ("Iou", ConId), ("with", ContractKeyword), ("issuer", VarId), (":", ReservedOp),
            ("Party", ConId), (";", Punctuation), ("amount", VarId), (":", ReservedOp), ("Decimal", ConId), ("where", Keyword),
        ]);
        Significant("  nonconsuming choice Iou_Transfer : ContractId Iou").ShouldBe(
        [
            ("nonconsuming", ChoiceModifier), ("choice", ContractKeyword), ("Iou_Transfer", ConId), (":", ReservedOp),
            ("ContractId", ConId), ("Iou", ConId),
        ]);
    }

    [Fact, Trait("Category", "Unit")]
    public void Whitespace_runs_are_single_tokens_and_tokens_are_appended()
    {
        var tokens = new List<DamlToken> { new(0, 1, Error) };

        DamlLexer.LexLine(System.MemoryExtensions.AsSpan("  \t x"), default, tokens);

        tokens.ShouldBe([new DamlToken(0, 1, Error), new DamlToken(0, 4, Whitespace), new DamlToken(4, 1, VarId)]);
    }

    [Fact, Trait("Category", "Unit")]
    public void An_empty_line_produces_nothing_and_keeps_the_state()
    {
        var inComment = new LexState(LexMode.BlockComment, 3);

        Lex("").Tokens.ShouldBeEmpty();
        Lex("", inComment).End.ShouldBe(inComment);
    }

    [Fact, Trait("Category", "Unit")]
    public void Characters_outside_the_BMP_are_one_character_in_literals_and_names()
    {
        // U+1F600 and U+1D465 are surrogate pairs in UTF-16.
        Single("'😀'").ShouldBe(Char);
        Significant("𝑥 = 1").ShouldBe([("𝑥", VarId), ("=", ReservedOp), ("1", Number)]);
        Significant("f𝟙 x").ShouldBe([("f𝟙", VarId), ("x", VarId)]);
    }
}
