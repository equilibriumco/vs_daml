using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Equilibrium.Daml.VisualStudio.Core.Syntax;

namespace Equilibrium.Daml.VisualStudio.Benchmarks;

/// <summary>
/// The lexer on a 5000-line file: lexing all of it, as when a buffer opens, and re-lexing after
/// a one-line edit, as the classifier and the token cache do on every keystroke, through the
/// same caches the editor uses.
/// </summary>
[MemoryDiagnoser]
public class LexerBenchmarks
{
    private readonly List<DamlToken> _tokens = new(64);
    private string[] _lines = [];
    private ArrayLines _text = new([]);
    private LexerSyntaxProvider _classifier = new(new ArrayLines([]));
    private readonly LineTokensCache _cache = new();
    private int _editedLine;
    private string _original = "";
    private string _edited = "";
    private bool _editApplied;

    /// <summary>Loads the fixture and warms both caches over all of it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        using (var stream = typeof(LexerBenchmarks).Assembly.GetManifestResourceStream("large-5k.daml")
            ?? throw new InvalidOperationException("The large-5k.daml resource is missing."))
        using (var reader = new StreamReader(stream))
        {
            _lines = reader.ReadToEnd().TrimEnd('\n').Split('\n');
        }
        _text = new ArrayLines(_lines);

        _classifier = new LexerSyntaxProvider(_text);
        for (var line = 0; line < _lines.Length; line++)
        {
            _classifier.GetClassifications(line);
        }
        _cache.Reset(_text);

        // A typical keystroke: an ordinary line in the middle of the file changes.
        _editedLine = Array.FindIndex(_lines, _lines.Length / 2, l => l.TrimStart().StartsWith("ensure ", StringComparison.Ordinal));
        _original = _lines[_editedLine];
        _edited = _original.Replace("0.0", "10.0");
    }

    /// <summary>Lexes every line, carrying the state from line to line.</summary>
    [Benchmark]
    public int LexWholeFile()
    {
        var count = 0;
        var state = default(LexState);
        foreach (var line in _lines)
        {
            _tokens.Clear();
            state = DamlLexer.LexLine(line.AsSpan(), state, _tokens);
            count += _tokens.Count;
        }
        return count;
    }

    /// <summary>Lexes every line into the records the token features work on.</summary>
    [Benchmark]
    public int LexWholeFileIntoTokens() => new LineTokensCache().Reset(_text).Count;

    /// <summary>Applies or undoes the edit, then brings the classifier's line states up to date.</summary>
    [Benchmark]
    public int RelexAfterOneLineEdit()
    {
        var range = _classifier.ApplyEdit(_text, ToggleEdit());
        return range.LastLine - range.FirstLine + 1;
    }

    /// <summary>Applies or undoes the edit, then brings the token cache up to date.</summary>
    [Benchmark]
    public int RelexTokensAfterOneLineEdit()
    {
        var range = _cache.Update(_text, ToggleEdit());
        return range.LastLine - range.FirstLine + 1;
    }

    private LineEdit ToggleEdit()
    {
        _editApplied = !_editApplied;
        _lines[_editedLine] = _editApplied ? _edited : _original;
        return new LineEdit(_editedLine, _editedLine, 0);
    }

    private sealed class ArrayLines(string[] lines) : ILineSource
    {
        public int LineCount => lines.Length;

        public string GetLineText(int line) => lines[line];
    }
}
