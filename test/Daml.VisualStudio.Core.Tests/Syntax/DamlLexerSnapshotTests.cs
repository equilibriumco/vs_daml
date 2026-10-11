using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Syntax;
using Shouldly;
using VerifyXunit;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Syntax;

// Whole files lexed line by line with the state carried over, rendered one token per line so a
// snapshot diff points at the exact token that changed.
public class DamlLexerSnapshotTests
{
    private static readonly string FixtureFolder = Path.Combine(Repository.Root, "test", "fixtures", "lexer");

    // Third-party files are read where they live rather than copied into the fixtures.
    private static readonly Dictionary<string, string> ThirdParty = new()
    {
        ["TestGrammar"] = Path.Combine(Repository.Root, "third_party", "daml-extension", "TestGrammar.daml"),
        ["mini-demo-Asset"] = Repository.Sample(@"mixed-dotnet\daml\daml\MiniDemo\Asset.daml"),
        ["mini-demo-AssetTest"] = Repository.Sample(@"mixed-dotnet\daml\test\daml\MiniDemo\AssetTest.daml"),
    };

    public static TheoryData<string> Fixtures()
    {
        var names = new TheoryData<string>();
        foreach (var name in Directory.GetFiles(FixtureFolder, "*.daml").Select(Path.GetFileNameWithoutExtension).Concat(ThirdParty.Keys).OrderBy(n => n, System.StringComparer.Ordinal))
        {
            names.Add(name!);
        }
        return names;
    }

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(Fixtures))]
    public Task Fixture_tokens(string fixture)
    {
        var path = ThirdParty.TryGetValue(fixture, out var thirdParty) ? thirdParty : Path.Combine(FixtureFolder, fixture + ".daml");
        return Verifier.Verify(Render(File.ReadAllText(path, Encoding.UTF8)), extension: "txt").UseParameters(fixture);
    }

    [Fact, Trait("Category", "Unit")]
    public void There_are_enough_fixtures_to_be_worth_snapshotting() =>
        Fixtures().Count.ShouldBeGreaterThanOrEqualTo(15);

    private static string Render(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var lineCount = lines.Length > 0 && lines[lines.Length - 1].Length == 0 ? lines.Length - 1 : lines.Length;
        var output = new StringBuilder();
        var state = default(LexState);
        var tokens = new List<DamlToken>();
        for (var number = 1; number <= lineCount; number++)
        {
            var line = lines[number - 1];
            tokens.Clear();
            state = DamlLexer.LexLine(System.MemoryExtensions.AsSpan(line), state, tokens);
            foreach (var token in tokens.Where(t => t.Kind != DamlTokenKind.Whitespace))
            {
                output.Append('L').Append(number).Append(" [").Append(token.Start).Append("..").Append(token.Start + token.Length).Append(") ")
                    .Append(token.Kind).Append(" \"").Append(line, token.Start, token.Length).Append('"').Append('\n');
            }
            if (state.Mode != LexMode.Normal)
            {
                output.Append('L').Append(number).Append(" end ").Append(state.Mode).Append(" depth ").Append(state.CommentDepth).Append('\n');
            }
        }
        return output.ToString();
    }
}
