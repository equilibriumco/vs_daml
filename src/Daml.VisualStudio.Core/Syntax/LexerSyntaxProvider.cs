using System;
using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Syntax;

/// <summary>
/// Source of syntax colouring for one text as it changes. The editor talks only to this, so a
/// different engine (a full parser, say) can replace the lexer without touching the editor side.
/// Not thread-safe: callers serialise access.
/// </summary>
public interface IDamlSyntaxProvider
{
    /// <summary>The coloured spans of <paramref name="line"/> in the current text.</summary>
    IReadOnlyList<ClassifiedSpan> GetClassifications(int line);

    /// <summary>
    /// Switches to <paramref name="text"/>, the current text with <paramref name="edit"/> applied,
    /// and returns the lines whose colouring may have changed.
    /// </summary>
    LineRange ApplyEdit(ILineSource text, LineEdit edit);

    /// <summary>Starts over on <paramref name="text"/>, e.g. after a reload.</summary>
    void Reset(ILineSource text);
}

/// <summary><see cref="IDamlSyntaxProvider"/> over <see cref="DamlLexer"/> and a <see cref="LineStateCache"/>.</summary>
public sealed class LexerSyntaxProvider : IDamlSyntaxProvider
{
    private readonly LineStateCache _states = new();
    private readonly List<DamlToken> _tokens = [];
    private ILineSource _text;

    /// <summary>Creates a provider for <paramref name="text"/>.</summary>
    public LexerSyntaxProvider(ILineSource text) =>
        _text = text ?? throw new ArgumentNullException(nameof(text));

    /// <inheritdoc />
    public IReadOnlyList<ClassifiedSpan> GetClassifications(int line)
    {
        var text = _text.GetLineText(line);
        _tokens.Clear();
        DamlLexer.LexLine(text.AsSpan(), _states.StateAt(_text, line), _tokens);

        var spans = new List<ClassifiedSpan>(_tokens.Count);
        foreach (var token in _tokens)
        {
            if (ClassificationMapper.Map(token, text.AsSpan()) is { } classification)
            {
                spans.Add(new ClassifiedSpan(token.Start, token.Length, classification));
            }
        }
        return spans;
    }

    /// <inheritdoc />
    public LineRange ApplyEdit(ILineSource text, LineEdit edit)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        return _states.Update(text, edit);
    }

    /// <inheritdoc />
    public void Reset(ILineSource text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _states.Clear();
    }
}
