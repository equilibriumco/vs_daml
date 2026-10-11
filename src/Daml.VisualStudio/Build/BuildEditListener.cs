using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Build;

/// <summary>
/// Once a Daml file is edited, its build errors may no longer be true, so they leave the
/// Error List; the language server's live diagnostics carry on.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class BuildEditListener : IWpfTextViewCreationListener
{
    private readonly DamlBuildService _builds;
    private readonly ITextDocumentFactoryService _documents;

    [ImportingConstructor]
    public BuildEditListener(DamlBuildService builds, ITextDocumentFactoryService documents)
    {
        _builds = builds;
        _documents = documents;
    }

    public void TextViewCreated(IWpfTextView textView)
    {
        var buffer = textView.TextDataModel.DocumentBuffer;
        if (!_documents.TryGetTextDocument(buffer, out var document))
        {
            return;
        }

        void OnChanged(object sender, TextContentChangedEventArgs e) => _builds.Store.ClearFile(document.FilePath);
        buffer.Changed += OnChanged;
        textView.Closed += (_, _) => buffer.Changed -= OnChanged;
    }
}
