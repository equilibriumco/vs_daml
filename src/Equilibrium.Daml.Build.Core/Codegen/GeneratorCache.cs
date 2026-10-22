using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.FileSystem;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// The private dpm home a pinned generator runs from, one per digest, so two pins can never
/// share a cached component (dpm caches components by name alone) and the user's own dpm home
/// is never touched. dpm leaves the pinned OCI index and the platform manifest in it as blobs
/// named by their SHA-256, and the manifest lists every file of the component with its size
/// and digest, which is what <see cref="Check"/> holds the unpacked files to.
/// </summary>
public sealed class GeneratorCache
{
    private const string ReadyMarker = ".daml-ready";
    private const string TitleAnnotation = "org.opencontainers.image.title";

    private readonly string _root;

    /// <summary>A cache for <paramref name="pin"/> under <paramref name="root"/>.</summary>
    /// <param name="root">The folder holding one home per digest, usually <see cref="DefaultRoot"/>.</param>
    /// <param name="pin">The generator.</param>
    public GeneratorCache(string root, CodegenComponentPin pin)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        Pin = pin ?? throw new ArgumentNullException(nameof(pin));
    }

    /// <summary>The generator this cache is for.</summary>
    public CodegenComponentPin Pin { get; }

    /// <summary>The dpm home the generator runs with: <c>DPM_HOME</c>.</summary>
    public string Home => Path.Combine(_root, Pin.Digest);

    /// <summary>Where dpm unpacks the component.</summary>
    public string Component => Path.Combine(Home, "cache", "components", Pin.Repository.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Whether a run already found this cache complete for this pin.</summary>
    public bool IsMarkedReady
    {
        get
        {
            var marker = Path.Combine(Home, ReadyMarker);
            return File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), Pin.Reference, StringComparison.Ordinal);
        }
    }

    /// <summary><c>%LOCALAPPDATA%\EquilibriumDamlTools\codegen</c>.</summary>
    public static string DefaultRoot(Func<string, string?> environment)
    {
        if (environment is null)
        {
            throw new ArgumentNullException(nameof(environment));
        }
        var localAppData = environment("LOCALAPPDATA");
        return string.IsNullOrEmpty(localAppData)
            ? throw new InvalidOperationException("LOCALAPPDATA isn't set, so there is nowhere to keep the code generator.")
            : Path.Combine(localAppData!, "EquilibriumDamlTools", "codegen");
    }

    /// <summary>Records that the cache was found complete for this pin.</summary>
    public void MarkReady() => File.WriteAllText(Path.Combine(Home, ReadyMarker), Pin.Reference);

    /// <summary>
    /// Holds the cache to the pin: the index blob has the pinned digest, the windows/amd64
    /// manifest it names was downloaded intact, and every file the manifest lists is unpacked
    /// with its size and, when <paramref name="hashFiles"/>, its digest.
    /// </summary>
    /// <param name="hashFiles">Hash every unpacked file, not only compare sizes; slower, but it catches a file changed in place.</param>
    public GeneratorCacheState Check(bool hashFiles)
    {
        var index = Blob(Pin.Digest);
        if (!File.Exists(index))
        {
            return new GeneratorCacheState(GeneratorCacheStatus.Missing, "Nothing of the pinned component was downloaded.", BlobsDamaged: false);
        }
        if (!string.Equals(Sha256(index), Pin.Digest, StringComparison.Ordinal))
        {
            return Damaged("The downloaded index doesn't match the pinned digest.", blobs: true);
        }
        var platform = WindowsManifest(Json(index));
        if (platform is null)
        {
            return new GeneratorCacheState(GeneratorCacheStatus.Unsupported, "The pinned component has no windows/amd64 build.", BlobsDamaged: false);
        }
        var manifest = Blob(platform);
        if (!File.Exists(manifest))
        {
            return Damaged($"The windows/amd64 manifest sha256:{platform} wasn't downloaded.", blobs: true);
        }
        if (!string.Equals(Sha256(manifest), platform, StringComparison.Ordinal))
        {
            return Damaged($"The windows/amd64 manifest sha256:{platform} doesn't match its digest.", blobs: true);
        }
        if (Layers(Json(manifest)) is not { } layers)
        {
            return Damaged($"The windows/amd64 manifest sha256:{platform} lists no files.", blobs: true);
        }
        foreach (var (name, size, digest) in layers)
        {
            if (Path.IsPathRooted(name) || name.Split('/', '\\').Contains(".."))
            {
                return Damaged($"The windows/amd64 manifest names a file outside the component: {name}.", blobs: true);
            }
            var file = new FileInfo(Path.Combine(Component, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.Exists)
            {
                return Damaged($"{name} is missing.", blobs: false);
            }
            if (file.Length != size)
            {
                return Damaged($"{name} is {file.Length} bytes, but the pinned component has {size}.", blobs: false);
            }
            if (hashFiles && !string.Equals(Sha256(file.FullName), digest, StringComparison.Ordinal))
            {
                return Damaged($"{name} doesn't match the pinned component.", blobs: false);
            }
        }
        return new GeneratorCacheState(GeneratorCacheStatus.Ready, null, BlobsDamaged: false);
    }

    private static GeneratorCacheState Damaged(string reason, bool blobs) => new(GeneratorCacheStatus.Damaged, reason, blobs);

    private string Blob(string digest) => Path.Combine(Home, "cache", "oci-layout", "blobs", "sha256", digest);

    // The digest of the index entry for windows/amd64, without its "sha256:" prefix.
    private static string? WindowsManifest(YamlMappingNode? index)
    {
        foreach (var entry in Children(index, "manifests"))
        {
            if (entry is YamlMappingNode manifest
                && Child(manifest, "platform") is YamlMappingNode platform
                && Text(platform, "os") == "windows"
                && Text(platform, "architecture") == "amd64"
                && Text(manifest, "digest") is { } digest
                && digest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return digest.Substring("sha256:".Length);
            }
        }
        return null;
    }

    private static List<(string Name, long Size, string Digest)>? Layers(YamlMappingNode? manifest)
    {
        var layers = new List<(string, long, string)>();
        foreach (var entry in Children(manifest, "layers"))
        {
            if (entry is not YamlMappingNode layer
                || Child(layer, "annotations") is not YamlMappingNode annotations
                || Text(annotations, TitleAnnotation) is not { Length: > 0 } name
                || !long.TryParse(Text(layer, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || Text(layer, "digest") is not { } digest
                || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return null;
            }
            layers.Add((name, size, digest.Substring("sha256:".Length)));
        }
        return layers.Count > 0 ? layers : null;
    }

    // OCI documents are JSON, which YAML reads as it is.
    private static YamlMappingNode? Json(string path)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(path)));
            return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    private static YamlNode? Child(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static IEnumerable<YamlNode> Children(YamlMappingNode? node, string key) =>
        Child(node, key) is YamlSequenceNode sequence ? sequence.Children : [];

    private static string? Text(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static string Sha256(string path) => Hashing.Sha256File(path);
}
