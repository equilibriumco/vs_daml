namespace Daml.VisualStudio.LanguageServer.Messages;

/// <summary>New contents of a virtual resource, e.g. Script results as HTML.</summary>
/// <param name="Uri">The resource, a <c>daml://</c> URI.</param>
/// <param name="Contents">Its new contents.</param>
public sealed record VirtualResourceChangedParams(string Uri, string Contents);

/// <summary>A short note about a virtual resource, e.g. why it couldn't be produced.</summary>
/// <param name="Uri">The resource.</param>
/// <param name="Note">The note.</param>
public sealed record VirtualResourceNoteParams(string Uri, string Note);

/// <summary>A virtual resource is still being produced.</summary>
/// <param name="Uri">The resource.</param>
/// <param name="MillisecondsPassed">Time spent so far.</param>
/// <param name="StartedAt">When it started, in milliseconds since the Unix epoch.</param>
public sealed record VirtualResourceProgressParams(string Uri, long MillisecondsPassed, long StartedAt);

/// <summary>Stage of an SDK installation.</summary>
public enum SdkInstallProgressKind
{
    /// <summary>The installation started.</summary>
    Begin,

    /// <summary>The installation moved on.</summary>
    Report,

    /// <summary>The installation finished, successfully or not.</summary>
    End,
}

/// <summary>Progress of an SDK installation the server started.</summary>
/// <param name="SdkVersionIdentifier">The SDK being installed, as the server identifies it.</param>
/// <param name="SdkVersionRendered">The SDK's name for people.</param>
/// <param name="Kind">The stage.</param>
/// <param name="Progress">Percent complete, as the server reports it.</param>
public sealed record SdkInstallProgressParams(string SdkVersionIdentifier, string SdkVersionRendered, SdkInstallProgressKind Kind, int Progress);

/// <summary>Receives virtual resource notifications.</summary>
public interface IVirtualResourceSink
{
    /// <summary>A resource has new contents.</summary>
    void Changed(VirtualResourceChangedParams change);

    /// <summary>The server has a note about a resource.</summary>
    void Noted(VirtualResourceNoteParams note);

    /// <summary>A resource is still being produced.</summary>
    void Progressed(VirtualResourceProgressParams progress);
}

/// <summary>Receives SDK installation progress.</summary>
public interface ISdkInstallProgressSink
{
    /// <summary>An installation moved to another stage.</summary>
    void Report(SdkInstallProgressParams progress);
}
