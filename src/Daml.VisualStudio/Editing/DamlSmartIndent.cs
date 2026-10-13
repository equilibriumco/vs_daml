using System.ComponentModel.Composition;
using System.Linq;
using Daml.VisualStudio.Core.Syntax;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Editing;

/// <summary>Smart indent for Daml views.</summary>
[Export(typeof(ISmartIndentProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
internal sealed class DamlSmartIndentProvider : ISmartIndentProvider
{
    public ISmartIndent CreateSmartIndent(ITextView textView) => new DamlSmartIndent(textView);
}

/// <summary>
/// Puts the caret of a new line where the code around it says it belongs. The whole file is
/// lexed for each new line, which takes a few milliseconds even for thousands of lines.
/// </summary>
internal sealed class DamlSmartIndent : ISmartIndent
{
    private readonly ITextView _view;

    public DamlSmartIndent(ITextView view) => _view = view;

    public int? GetDesiredIndentation(ITextSnapshotLine line)
    {
        var lines = LineTokens.Lex(line.Snapshot.Lines.Select(l => l.GetText()));
        return IndentRules.GetDesiredIndentation(lines, line.LineNumber, _view.Options.GetOptionValue(DefaultOptions.IndentSizeOptionId));
    }

    public void Dispose()
    {
    }
}
