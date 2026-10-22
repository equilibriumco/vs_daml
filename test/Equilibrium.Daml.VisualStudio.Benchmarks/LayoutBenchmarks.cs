using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Equilibrium.Daml.VisualStudio.Core.Syntax;

namespace Equilibrium.Daml.VisualStudio.Benchmarks;

/// <summary>
/// The block tree of a 5000-line file, as folding and smart indent rebuild it after an edit:
/// from tokens already lexed, and from plain text for comparison.
/// </summary>
[MemoryDiagnoser]
public class LayoutBenchmarks
{
    private string[] _text = [];
    private IReadOnlyList<LineTokens> _lines = [];

    /// <summary>Loads and lexes the fixture.</summary>
    [GlobalSetup]
    public void Setup()
    {
        using (var stream = typeof(LayoutBenchmarks).Assembly.GetManifestResourceStream("large-5k.daml")
            ?? throw new InvalidOperationException("The large-5k.daml resource is missing."))
        using (var reader = new StreamReader(stream))
        {
            _text = reader.ReadToEnd().TrimEnd('\n').Split('\n');
        }
        _lines = new LineTokensCache().Reset(new ArrayLines(_text));
    }

    /// <summary>The tree from lexed lines.</summary>
    [Benchmark]
    public DamlBlock AnalyzeLexedFile() => LayoutAnalyzer.Analyze(_lines);

    /// <summary>Lexing and the tree together.</summary>
    [Benchmark]
    public DamlBlock LexAndAnalyzeFile() => LayoutAnalyzer.Analyze(LineTokens.Lex(_text));

    /// <summary>Smart indent for Enter in the middle of the file, from lexed lines, as on every keystroke.</summary>
    [Benchmark]
    public int? IndentInTheMiddle() => IndentRules.GetDesiredIndentation(_lines, _lines.Count / 2, indentSize: 2);
    private sealed class ArrayLines(string[] lines) : ILineSource
    {
        public int LineCount => lines.Length;
        public string GetLineText(int line) => lines[line];
    }

}
