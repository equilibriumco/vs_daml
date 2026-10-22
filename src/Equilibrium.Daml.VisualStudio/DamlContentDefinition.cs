using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Equilibrium.Daml.VisualStudio;

/// <summary>
/// The "daml" content type. It derives from the LSP client's code-remote content type,
/// otherwise Visual Studio never hands .daml buffers to a language client.
/// </summary>
internal static class DamlContentDefinition
{
    public const string ContentTypeName = "daml";

    // MEF fills these by reflection; they are never read or written by our code.
    [Export]
    [Name(ContentTypeName)]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "MEF export field.")]
    internal static ContentTypeDefinition? DamlContentType;

    [Export]
    [FileExtension(".daml")]
    [ContentType(ContentTypeName)]
    [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "MEF export field.")]
    internal static FileExtensionToContentTypeDefinition? DamlFileExtension;
}
