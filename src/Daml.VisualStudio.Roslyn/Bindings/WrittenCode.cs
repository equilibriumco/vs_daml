using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// The code the generator wrote, for when Visual Studio still holds an older build's: a build swaps
/// a generation's folder whole, and the editor can go on holding the files of the build before,
/// which state the previous package, so nothing in them matches the new manifest.
/// </summary>
internal static class WrittenCode
{
    /// <summary>
    /// The semantic model of <paramref name="document"/> with the text of its file on disk; null when
    /// the workspace already holds that text, or the file can't be read.
    /// </summary>
    public static async Task<SemanticModel?> ModelAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.FilePath is not { } path)
        {
            return null;
        }
        SourceText written;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            written = SourceText.From(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return text.ContentEquals(written) ? null : await document.WithText(written).GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
    }
}
