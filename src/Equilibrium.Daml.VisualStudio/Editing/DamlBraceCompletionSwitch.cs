using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Equilibrium.Daml.VisualStudio.Editing;

/// <summary>
/// Switches brace completion on for Daml views. The pairs themselves come from the language
/// configuration file, through the editor's TextMate brace completion that serves every code
/// content type; but the editor keeps the switch per language, behind "Automatic brace
/// completion" in Tools > Options, and Daml has no page there, so nothing would close without
/// this. Typing the partner steps over it, and Backspace after the opening one removes both.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class DamlBraceCompletionSwitch : IWpfTextViewCreationListener
{
    public void TextViewCreated(IWpfTextView textView) =>
        textView.Options.SetOptionValue(DefaultTextViewOptions.BraceCompletionEnabledOptionId, true);
}
