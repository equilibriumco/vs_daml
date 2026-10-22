using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>
/// Which C# declarations stand for which Daml symbols, across the projects of a solution. One
/// symbol may have many declarations: several projects, namespaces and target frameworks can each
/// generate it. An index never changes; every change makes a new one with a higher version, so a
/// reader keeps a consistent view however long it takes. Entries are kept per project and file,
/// the unit they are read again in.
/// </summary>
public sealed class BindingIndex
{
    private static readonly DocumentKeyComparer Documents = new();

    private readonly Dictionary<(string Project, string File), IReadOnlyList<BindingLocation>> _documents;
    private readonly Lazy<Lookups> _lookups;

    private BindingIndex(Dictionary<(string, string), IReadOnlyList<BindingLocation>> documents, long version)
    {
        _documents = documents;
        Version = version;
        _lookups = new Lazy<Lookups>(() => new Lookups(_documents.Values.SelectMany(l => l)));
    }

    /// <summary>An index with nothing in it, version 0.</summary>
    public static BindingIndex Empty { get; } = new(new Dictionary<(string, string), IReadOnlyList<BindingLocation>>(Documents), 0);

    /// <summary>The version: higher for every change made from this index on.</summary>
    public long Version { get; }

    /// <summary>How many declarations the index holds, counting each project's separately.</summary>
    public int Count => _documents.Values.Sum(l => l.Count);

    /// <summary>The declarations of <paramref name="symbol"/>, one per project that has each.</summary>
    public IReadOnlyList<BindingLocation> LocationsOf(DamlSymbolId symbol) =>
        _lookups.Value.BySymbol.TryGetValue(symbol, out var locations) ? locations : [];

    /// <summary>The symbols the declaration <paramref name="declarationId"/> stands for in a project; normally one.</summary>
    public IReadOnlyList<DamlSymbolId> SymbolsOf(string projectKey, string declarationId) =>
        _lookups.Value.ByDeclaration.TryGetValue((projectKey, declarationId), out var symbols) ? symbols : [];

    /// <summary>The generations built from the Daml project of <paramref name="sourceManifest"/>, each once.</summary>
    public IReadOnlyList<GenerationProvenance> GenerationsFrom(string sourceManifest) =>
        _lookups.Value.Generations
            .Where(g => g.Source is { } source && string.Equals(source.ManifestPath, sourceManifest, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// What the index holds, in a line for the log: how many declarations, and the generations
    /// (folder and the start of the package ID) they come from.
    /// </summary>
    public string Describe()
    {
        if (Count == 0)
        {
            return "no C# declarations";
        }
        var generations = _lookups.Value.Generations
            .Select(g => $"{Path.GetFileName(g.Folder)} (package {g.MainPackageId.Substring(0, Math.Min(8, g.MainPackageId.Length))})")
            .Distinct()
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase);
        return $"{Count} C# declaration{(Count == 1 ? "" : "s")} from {string.Join(", ", generations)}";
    }

    /// <summary>The physical declarations of <paramref name="symbol"/>: a file linked into several projects counts once.</summary>
    public IReadOnlyList<BindingDeclaration> DeclarationsOf(DamlSymbolId symbol) =>
        LocationsOf(symbol)
            .GroupBy(l => (File: l.FilePath.ToUpperInvariant(), l.DeclarationId, l.Start, l.Length))
            .Select(g => new BindingDeclaration(g.First().FilePath, g.Key.DeclarationId, g.Key.Start, g.Key.Length, g.ToList()))
            .ToList();

    /// <summary>The index with what <paramref name="filePath"/> of a project holds now, in place of what it held before.</summary>
    /// <exception cref="ArgumentException">A location is of another project or file.</exception>
    public BindingIndex WithDocument(string projectKey, string filePath, IReadOnlyList<BindingLocation> locations) =>
        WithDocuments([(projectKey, filePath, locations)]);

    /// <summary>The index with what several files hold now, as one change; a file holding nothing leaves the index.</summary>
    /// <exception cref="ArgumentException">A location is of another project or file than the one it is given for.</exception>
    public BindingIndex WithDocuments(IEnumerable<(string ProjectKey, string FilePath, IReadOnlyList<BindingLocation> Locations)> changes)
    {
        var documents = new Dictionary<(string, string), IReadOnlyList<BindingLocation>>(_documents, Documents);
        foreach (var (projectKey, filePath, locations) in changes ?? throw new ArgumentNullException(nameof(changes)))
        {
            if (locations.Any(l => l.ProjectKey != projectKey || !string.Equals(l.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Every location has to be in {filePath} of project {projectKey}.", nameof(changes));
            }
            if (locations.Count == 0)
            {
                documents.Remove((projectKey, filePath));
            }
            else
            {
                documents[(projectKey, filePath)] = locations.ToList();
            }
        }
        return new BindingIndex(documents, Version + 1);
    }

    /// <summary>An empty index that is still a newer version than this one, to read a whole solution into.</summary>
    public BindingIndex Cleared() => new(new Dictionary<(string, string), IReadOnlyList<BindingLocation>>(Documents), Version + 1);

    /// <summary>The index without what <paramref name="filePath"/> of a project held; this one when it held nothing.</summary>
    public BindingIndex WithoutDocument(string projectKey, string filePath) =>
        _documents.ContainsKey((projectKey, filePath)) ? WithDocument(projectKey, filePath, []) : this;

    /// <summary>The index without anything of <paramref name="projectKey"/>; this one when it held nothing of it.</summary>
    public BindingIndex WithoutProject(string projectKey)
    {
        var remaining = _documents.Where(d => d.Key.Project != projectKey).ToList();
        if (remaining.Count == _documents.Count)
        {
            return this;
        }
        var documents = new Dictionary<(string, string), IReadOnlyList<BindingLocation>>(Documents);
        foreach (var document in remaining)
        {
            documents.Add(document.Key, document.Value);
        }
        return new BindingIndex(documents, Version + 1);
    }

    private sealed class Lookups
    {
        public Lookups(IEnumerable<BindingLocation> locations)
        {
            foreach (var location in locations)
            {
                Add(BySymbol, location.Symbol, location);
                if (!ByDeclaration.TryGetValue((location.ProjectKey, location.DeclarationId), out var symbols))
                {
                    ByDeclaration[(location.ProjectKey, location.DeclarationId)] = symbols = [];
                }
                if (!symbols.Contains(location.Symbol))
                {
                    symbols.Add(location.Symbol);
                }
                if (!Generations.Contains(location.Generation))
                {
                    Generations.Add(location.Generation);
                }
            }
        }

        public List<GenerationProvenance> Generations { get; } = [];

        public Dictionary<DamlSymbolId, List<BindingLocation>> BySymbol { get; } = [];

        public Dictionary<(string Project, string DeclarationId), List<DamlSymbolId>> ByDeclaration { get; } = [];

        private static void Add<TKey>(Dictionary<TKey, List<BindingLocation>> map, TKey key, BindingLocation location)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }
            list.Add(location);
        }
    }

    // Projects as the host names them; files as Windows compares paths.
    private sealed class DocumentKeyComparer : IEqualityComparer<(string Project, string File)>
    {
        public bool Equals((string Project, string File) x, (string Project, string File) y) =>
            string.Equals(x.Project, y.Project, StringComparison.Ordinal) && string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Project, string File) key) =>
            StringComparer.Ordinal.GetHashCode(key.Project) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(key.File);
    }
}
