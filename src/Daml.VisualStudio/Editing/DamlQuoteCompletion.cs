using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Utilities;

namespace Daml.VisualStudio.Editing;

/// <summary>
/// Closes double quotes in Daml buffers. Brackets close through the language configuration, but
/// VS won't close a quote from there for a language without a TextMate grammar. Wrapping a
/// selection in quotes stays with the language configuration.
/// </summary>
[Export(typeof(IBraceCompletionDefaultProvider))]
[ContentType(DamlContentDefinition.ContentTypeName)]
[BracePair('"', '"', SupportsAutoSurround = false)]
internal sealed class DamlQuoteCompletion : IBraceCompletionDefaultProvider
{
}
