using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Daml.VisualStudio.Core.Syntax;

namespace Daml.VisualStudio.Benchmarks;

/// <summary>
/// The lexer on a 5000-line file: lexing all of it, as when a buffer opens, and re-lexing after
/// a one-line edit, as the classifier does on every keystroke. The edit benchmark keeps the
/// state at the start of every line and re-lexes from the edited line until the state it
/// produces matches what the next line already started with.
/// </summary>
[MemoryDiagnoser]
public class LexerBenchmarks
{
    private readonly List<DamlToken> _tokens = new(64);
    private string[] _lines = [];
    private LexState[] _startStates = [];
    private int _editedLine;
    private string _original = "";
    private string _edited = "";
    private bool _editApplied;

    /// <summary>Loads the fixture and records each line's starting state.</summary>
    [GlobalSetup]
    public void Setup()
    {
        using (var stream = typeof(LexerBenchmarks).Assembly.GetManifestResourceStream("large-5k.daml")
            ?? throw new InvalidOperationException("The large-5k.daml resource is missing."))
        using (var reader = new StreamReader(stream))
        {
            _lines = reader.ReadToEnd().TrimEnd('\n').Split('\n');
        }

        _startStates = new LexState[_lines.Length + 1];
        var state = default(LexState);
        for (var i = 0; i < _lines.Length; i++)
        {
            _startStates[i] = state;
            _tokens.Clear();
            state = DamlLexer.LexLine(_lines[i].AsSpan(), state, _tokens);
        }
        _startStates[_lines.Length] = state;

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

    /// <summary>Applies or undoes the edit, then re-lexes until the line states converge.</summary>
    [Benchmark]
    public int RelexAfterOneLineEdit()
    {
        _editApplied = !_editApplied;
        _lines[_editedLine] = _editApplied ? _edited : _original;

        var relexed = 0;
        var line = _editedLine;
        var state = _startStates[line];
        while (line < _lines.Length)
        {
            _tokens.Clear();
            state = DamlLexer.LexLine(_lines[line].AsSpan(), state, _tokens);
            relexed++;
            line++;
            if (state == _startStates[line])
            {
                break;
            }
            _startStates[line] = state;
        }
        return relexed;
    }
}
