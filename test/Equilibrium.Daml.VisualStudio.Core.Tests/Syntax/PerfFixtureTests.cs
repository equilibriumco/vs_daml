using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Syntax;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Syntax;

// The benchmark input has to be ordinary Daml for the timings to mean anything: big enough, and
// free of anything the lexer gives up on.
public class PerfFixtureTests
{
    [Fact, Trait("Category", "Unit")]
    public void The_benchmark_input_is_five_thousand_lines_of_clean_daml()
    {
        var lines = File.ReadAllLines(Path.Combine(Repository.Root, "test", "fixtures", "perf", "large-5k.daml"));
        var tokens = new List<DamlToken>();
        var state = default(LexState);
        var errors = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            tokens.Clear();
            state = DamlLexer.LexLine(lines[i].AsSpan(), state, tokens);
            errors.AddRange(tokens.Where(t => t.Kind == DamlTokenKind.Error).Select(t => $"line {i + 1}: '{lines[i].Substring(t.Start, t.Length)}'"));
        }

        lines.Length.ShouldBeGreaterThanOrEqualTo(5000);
        errors.ShouldBeEmpty();
        state.ShouldBe(default(LexState));
    }
}
