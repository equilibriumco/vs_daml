using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Daml.VisualStudio.Core.FileSystem;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Daml.VisualStudio.Core.Project;

/// <summary>
/// <see cref="IManifestReader"/> on YamlDotNet's node model, which keeps positions so every problem
/// can point at its line and column. A field of the wrong shape is reported and left empty; the
/// rest of the manifest still reads.
/// </summary>
public sealed class YamlManifestReader : IManifestReader
{
    // YamlDotNet prefixes messages with "(Line: 2, Col: 14, Idx: 23) - (Line: ...): "; the
    // diagnostic carries the position separately.
    private static readonly Regex PositionPrefix = new(@"^\(Line: \d+, Col: \d+, Idx: \d+\) - \(Line: \d+, Col: \d+, Idx: \d+\): ", RegexOptions.CultureInvariant);

    private readonly IDamlFileSystem _fileSystem;

    /// <summary>Creates a reader over <paramref name="fileSystem"/>.</summary>
    public YamlManifestReader(IDamlFileSystem fileSystem) =>
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <inheritdoc />
    public ManifestResult<DamlPackageManifest> ReadPackage(string manifestPath)
    {
        var path = DamlPath.Normalize(manifestPath);
        var file = new ManifestFile(path);
        if (file.Load(_fileSystem) is not { } root)
        {
            return new ManifestResult<DamlPackageManifest>(null, file.Diagnostics);
        }

        var manifest = new DamlPackageManifest
        {
            ManifestPath = path,
            PackageRoot = Path.GetDirectoryName(path)!,
            SdkVersion = file.Text(root, "sdk-version"),
            Name = file.Text(root, "name"),
            Version = file.Text(root, "version"),
            Source = file.Text(root, "source"),
            Components = file.TextList(root, "components"),
            Dependencies = file.TextList(root, "dependencies"),
            DataDependencies = file.TextList(root, "data-dependencies"),
            BuildOptions = file.TextList(root, "build-options"),
            ModulePrefixes = file.TextMap(root, "module-prefixes"),
        };
        return new ManifestResult<DamlPackageManifest>(manifest, file.Diagnostics);
    }

    /// <inheritdoc />
    public bool Exists(string manifestPath) => _fileSystem.FileExists(DamlPath.Normalize(manifestPath));

    /// <inheritdoc />
    public ManifestResult<MultiPackageManifest> ReadMultiPackage(string manifestPath)
    {
        var path = DamlPath.Normalize(manifestPath);
        var file = new ManifestFile(path);
        if (file.Load(_fileSystem) is not { } root)
        {
            return new ManifestResult<MultiPackageManifest>(null, file.Diagnostics);
        }

        var folder = Path.GetDirectoryName(path)!;
        var manifest = new MultiPackageManifest
        {
            ManifestPath = path,
            Root = folder,
            SdkVersion = file.Text(root, "sdk-version"),
            Packages = file.Paths(root, "packages", folder),
            Dars = file.Paths(root, "dars", folder),
        };
        return new ManifestResult<MultiPackageManifest>(manifest, file.Diagnostics);
    }

    // One manifest being read: collects diagnostics while fields are pulled out of the root map.
    private sealed class ManifestFile(string path)
    {
        private readonly List<ManifestDiagnostic> _diagnostics = [];

        public IReadOnlyList<ManifestDiagnostic> Diagnostics => _diagnostics;

        public YamlMappingNode? Load(IDamlFileSystem fileSystem)
        {
            string text;
            try
            {
                text = fileSystem.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(new ManifestDiagnostic(path, null, null, ManifestSeverity.Error, $"Can't read the manifest: {ex.Message}"));
                return null;
            }

            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(text));
            }
            catch (YamlException ex)
            {
                Error(ex.Start, PositionPrefix.Replace(ex.Message, ""));
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The scanner trips over some malformed input (an unclosed flow sequence, for one)
                // with a plain exception and no position. Still a bad manifest, not a crash.
                _diagnostics.Add(new ManifestDiagnostic(path, null, null, ManifestSeverity.Error, "The manifest is not valid YAML."));
                return null;
            }

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                var start = stream.Documents.Count > 0 ? stream.Documents[0].RootNode.Start : Mark.Empty;
                _diagnostics.Add(new ManifestDiagnostic(path, start.Line > 0 ? (int)start.Line : null, start.Line > 0 ? (int)start.Column : null,
                    ManifestSeverity.Error, "The manifest must be a set of 'key: value' entries."));
                return null;
            }
            return root;
        }

        public string? Text(YamlMappingNode root, string key) => Find(root, key) switch
        {
            null => null,
            YamlScalarNode scalar => Absent(scalar) ? null : scalar.Value,
            var node => Wrong<string>(node, $"'{key}' must be a single value."),
        };

        public List<string> TextList(YamlMappingNode root, string key) =>
            Entries(root, key).Select(e => e.Value).ToList();

        // The list's entries resolved against the manifest's folder; one that isn't a path at
        // all (illegal characters, too long) is reported and skipped rather than thrown.
        public List<string> Paths(YamlMappingNode root, string key, string folder)
        {
            var paths = new List<string>();
            foreach (var (value, start) in Entries(root, key))
            {
                try
                {
                    paths.Add(DamlPath.Resolve(folder, value));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    Error(start, $"'{key}' entry '{value}' isn't a valid path; it is skipped.");
                }
            }
            return paths;
        }

        private List<(string Value, Mark Start)> Entries(YamlMappingNode root, string key)
        {
            var items = new List<(string, Mark)>();
            switch (Find(root, key))
            {
                case null:
                    break;
                case YamlScalarNode scalar when Absent(scalar):
                    break;
                case YamlSequenceNode sequence:
                    foreach (var item in sequence.Children)
                    {
                        if (item is YamlScalarNode { Value: { } value })
                        {
                            items.Add((value, item.Start));
                        }
                        else
                        {
                            Error(item.Start, $"Every entry of '{key}' must be a single value; this one is skipped.");
                        }
                    }
                    break;
                case var node:
                    Error(node.Start, $"'{key}' must be a list ('- item' lines).");
                    break;
            }
            return items;
        }
        public Dictionary<string, string> TextMap(YamlMappingNode root, string key)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (Find(root, key))
            {
                case null:
                    break;
                case YamlScalarNode scalar when Absent(scalar):
                    break;
                case YamlMappingNode mapping:
                    foreach (var entry in mapping.Children)
                    {
                        if (entry.Key is YamlScalarNode { Value: { } name } && entry.Value is YamlScalarNode { Value: { } value })
                        {
                            map[name] = value;
                        }
                        else
                        {
                            Error(entry.Key.Start, $"Every entry of '{key}' must be 'name: value'; this one is skipped.");
                        }
                    }
                    break;
                case var node:
                    Error(node.Start, $"'{key}' must be a set of 'name: value' entries.");
                    break;
            }
            return map;
        }

        private static YamlNode? Find(YamlMappingNode root, string key) =>
            root.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

        // "key:" with nothing after it reads as an empty plain scalar: treat it as not given.
        private static bool Absent(YamlScalarNode scalar) =>
            scalar.Style == ScalarStyle.Plain && string.IsNullOrEmpty(scalar.Value);

        private T? Wrong<T>(YamlNode node, string message)
            where T : class
        {
            Error(node.Start, message);
            return null;
        }

        private void Error(Mark at, string message) =>
            _diagnostics.Add(new ManifestDiagnostic(path, (int)at.Line, (int)at.Column, ManifestSeverity.Error, message));
    }
}
