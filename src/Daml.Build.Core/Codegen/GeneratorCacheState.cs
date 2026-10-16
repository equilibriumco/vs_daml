namespace Daml.Build.Core.Codegen;

/// <summary>What the private cache of a pinned generator holds.</summary>
public enum GeneratorCacheStatus
{
    /// <summary>Nothing of this pin has been downloaded.</summary>
    Missing,

    /// <summary>The pinned component, complete.</summary>
    Ready,

    /// <summary>Something is there, but not the complete pinned component: a download or unpack was cut short, or a file changed.</summary>
    Damaged,

    /// <summary>The pin has no build this machine can run; no download changes that.</summary>
    Unsupported,
}

/// <summary>The outcome of checking a generator's private cache.</summary>
/// <param name="Status">What the cache holds.</param>
/// <param name="Reason">Why it isn't <see cref="GeneratorCacheStatus.Ready"/>, for messages; null when it is.</param>
/// <param name="BlobsDamaged">
/// Whether the downloaded index or manifest itself is wrong, so the download must start over,
/// rather than only the unpacked files, which dpm unpacks again from the blobs it has.
/// </param>
public sealed record GeneratorCacheState(GeneratorCacheStatus Status, string? Reason, bool BlobsDamaged);
