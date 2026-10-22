using System.Collections.Generic;
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

// The block tree of whole files, one block per line, indented by depth. Lines are shown counting
// from one so the snapshot reads like the file in an editor.
public class LayoutAnalyzerSnapshotTests
{
    private static readonly string FixtureFolder = Path.Combine(Repository.Root, "test", "fixtures", "layout");

    // Copies of real code from the samples, kept with the lexer fixtures so that editing a
    // sample (or opening it in an IDE test) can't change a snapshot behind the test's back.
    private static readonly Dictionary<string, string> Samples = new()
    {
        ["sample-single-package-Main"] = Path.Combine(Repository.Root, "test", "fixtures", "lexer", "sample-single-package-Main.daml"),
        ["sample-multi-package-Asset"] = Path.Combine(Repository.Root, "test", "fixtures", "lexer", "sample-multi-package-Asset.daml"),
        ["mini-demo-Asset"] = Path.Combine(Repository.Root, "test", "fixtures", "lexer", "mini-demo-Asset.daml"),
        ["mini-demo-AssetTest"] = Path.Combine(Repository.Root, "test", "fixtures", "lexer", "mini-demo-AssetTest.daml"),
    };

    public static TheoryData<string> Fixtures()
    {
        var names = new TheoryData<string>();
        foreach (var name in FixtureNames())
        {
            names.Add(name);
        }
        return names;
    }

    internal static IEnumerable<string> FixtureNames() =>
        Directory.GetFiles(FixtureFolder, "*.daml").Select(Path.GetFileNameWithoutExtension).Concat(Samples.Keys).OrderBy(n => n, System.StringComparer.Ordinal)!;

    internal static string FixtureText(string fixture) =>
        File.ReadAllText(Samples.TryGetValue(fixture, out var sample) ? sample : Path.Combine(FixtureFolder, fixture + ".daml"), Encoding.UTF8);

    [Theory, Trait("Category", "Unit")]
    [MemberData(nameof(Fixtures))]
    public Task Fixture_blocks(string fixture) =>
        Verifier.Verify(Render(LayoutAnalyzer.Analyze(LineTokens.Lex(FixtureText(fixture)))), extension: "txt").UseParameters(fixture);

    [Fact, Trait("Category", "Unit")]
    public void There_are_enough_fixtures_to_be_worth_snapshotting() =>
        Directory.GetFiles(FixtureFolder, "*.daml").Length.ShouldBeGreaterThanOrEqualTo(20);

    internal static string Render(DamlBlock root)
    {
        var output = new StringBuilder();
        Append(root, 0);
        return output.ToString();

        void Append(DamlBlock block, int depth)
        {
            output.Append(' ', depth * 2).Append(block.Kind);
            if (block.Name is not null)
            {
                output.Append(" \"").Append(block.Name).Append('"');
            }
            output.Append(" L").Append(block.StartLine + 1).Append("-L").Append(block.EndLine + 1);
            if (block.HeaderEndLine != block.StartLine)
            {
                output.Append(" header to L").Append(block.HeaderEndLine + 1);
            }
            output.Append(" body ").Append(block.BodyIndent).Append('\n');
            foreach (var child in block.Children)
            {
                Append(child, depth + 1);
            }
        }
    }
}
