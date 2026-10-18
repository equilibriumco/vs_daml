using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// Reads a solution's generated C# into a <see cref="BindingIndex"/>. Only documents whose files
/// belong to a generation are compiled for it, so projects without generated code cost a path
/// lookup per document. Assemblies the projects reference, NuGet packages of bindings among them,
/// aren't read: nothing in them proves which Daml sources they came from.
/// </summary>
public static class BindingIndexBuilder
{
    // Visual Studio loads a project once per target framework, named "App (net10.0)".
    private static readonly Regex PerFramework = new(@"^.+ \((?<framework>[^()]+)\)$");

    /// <summary>The index of every C# project in <paramref name="solution"/>.</summary>
    public static async Task<BindingIndex> BuildAsync(Solution solution, GenerationCatalog catalog, CancellationToken cancellationToken = default)
    {
        var index = BindingIndex.Empty;
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            index = await WithProjectAsync(index, project, catalog, cancellationToken).ConfigureAwait(false);
        }
        return index;
    }

    /// <summary><paramref name="index"/> with <paramref name="project"/> read again, in place of what it held of it.</summary>
    public static async Task<BindingIndex> WithProjectAsync(BindingIndex index, Project project, GenerationCatalog catalog, CancellationToken cancellationToken = default)
    {
        var changes = await ReadAsync(project.Documents, catalog, cancellationToken).ConfigureAwait(false);
        return index.WithoutProject(ProjectKey(project.Id)).WithDocuments(changes);
    }

    /// <summary><paramref name="index"/> with <paramref name="documents"/> read again, in place of what they held.</summary>
    public static async Task<BindingIndex> WithDocumentsAsync(BindingIndex index, IEnumerable<Document> documents, GenerationCatalog catalog, CancellationToken cancellationToken = default) =>
        index.WithDocuments(await ReadAsync(documents, catalog, cancellationToken).ConfigureAwait(false));

    /// <summary>How the index names a project: its Roslyn <see cref="ProjectId"/>, which lasts as long as the workspace.</summary>
    public static string ProjectKey(ProjectId id) => (id ?? throw new ArgumentNullException(nameof(id))).Id.ToString("N");

    /// <summary>The project with <paramref name="projectKey"/> in <paramref name="solution"/>, or null.</summary>
    public static Project? Project(Solution solution, string projectKey) =>
        Guid.TryParseExact(projectKey, "N", out var id) ? solution.GetProject(ProjectId.CreateFromSerialized(id)) : null;

    /// <summary>The target framework of a project loaded once per framework; null for a project with one.</summary>
    public static string? TargetFramework(Project project) =>
        PerFramework.Match((project ?? throw new ArgumentNullException(nameof(project))).Name) is { Success: true } match ? match.Groups["framework"].Value : null;

    private static async Task<List<(string, string, IReadOnlyList<BindingLocation>)>> ReadAsync(IEnumerable<Document> documents, GenerationCatalog catalog, CancellationToken cancellationToken)
    {
        var changes = new List<(string, string, IReadOnlyList<BindingLocation>)>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is not { } path)
            {
                continue;
            }
            var key = ProjectKey(document.Project.Id);
            if (catalog.Find(path) is null || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                changes.Add((key, path, []));
                continue;
            }
            var framework = TargetFramework(document.Project);
            var bindings = BindingReader.Read(model, catalog, cancellationToken).Bindings;
            changes.Add((key, path, bindings.Select(b => new BindingLocation(b.Symbol, key, framework, b.Generation, b.DeclarationId, b.FilePath, b.Span.Start, b.Span.Length)).ToList()));
        }
        return changes;
    }
}
