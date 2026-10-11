using System.IO;

namespace Daml.VisualStudio.LanguageServer.Protocol;

/// <summary>Limits applied to LSP traffic in both directions.</summary>
public sealed record LspAdapterSettings
{
    /// <summary>
    /// Largest message body accepted. A bigger announced length ends the connection instead of
    /// being allocated.
    /// </summary>
    public int MaxFrameBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Largest header block accepted.</summary>
    public int MaxHeaderBytes { get; init; } = 8 * 1024;
}

/// <summary>The byte stream doesn't follow LSP's <c>Content-Length</c> framing.</summary>
public sealed class LspFramingException : IOException
{
    /// <summary>Creates the exception.</summary>
    public LspFramingException(string message)
        : base(message)
    {
    }
}
