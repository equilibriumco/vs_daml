using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Daml.VisualStudio.Roslyn.Bindings;

/// <summary>
/// Reads a solution's generated C# into a <see cref="BindingIndex"/>. Only documents whose files
/// belong to a generation are compiled for it, so projects without generated code cost a path
/// lookup per document, and projects without the Daml runtime nothing. Assemblies the projects
/// reference, NuGet packages of bindings among them, aren't read: nothing in them proves which
/// Daml sources they came from.
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
            index = await WithProjectAsync(index, project, catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return index;
    }

    /// <summary><paramref name="index"/> with <paramref name="project"/> read again, in place of what it held of it.</summary>
    /// <param name="index">The index to start from.</param>
    /// <param name="project">The project.</param>
    /// <param name="catalog">The generations the files belong to.</param>
    /// <param name="leftOut">Told the path of each generated file whose declarations were left out, and why.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static async Task<BindingIndex> WithProjectAsync(BindingIndex index, Project project, GenerationCatalog catalog, Action<string, string>? leftOut = null, CancellationToken cancellationToken = default)
    {
        var changes = await ReadAsync(project.Documents, catalog, leftOut, cancellationToken).ConfigureAwait(false);
        return index.WithoutProject(ProjectKey(project.Id)).WithDocuments(changes);
    }

    /// <summary><paramref name="index"/> with <paramref name="documents"/> read again, in place of what they held.</summary>
    /// <param name="index">The index to start from.</param>
    /// <param name="documents">The documents.</param>
    /// <param name="catalog">The generations the files belong to.</param>
    /// <param name="leftOut">Told the path of each generated file whose declarations were left out, and why.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static async Task<BindingIndex> WithDocumentsAsync(BindingIndex index, IEnumerable<Document> documents, GenerationCatalog catalog, Action<string, string>? leftOut = null, CancellationToken cancellationToken = default) =>
        index.WithDocuments(await ReadAsync(documents, catalog, leftOut, cancellationToken).ConfigureAwait(false));

    /// <summary>How the index names a project: its Roslyn <see cref="ProjectId"/>, which lasts as long as the workspace.</summary>
    public static string ProjectKey(ProjectId id) => (id ?? throw new ArgumentNullException(nameof(id))).Id.ToString("N");

    /// <summary>The project with <paramref name="projectKey"/> in <paramref name="solution"/>, or null.</summary>
    public static Project? Project(Solution solution, string projectKey) =>
        Guid.TryParseExact(projectKey, "N", out var id) ? solution.GetProject(ProjectId.CreateFromSerialized(id)) : null;

    /// <summary>
    /// Whether <paramref name="project"/> compiles against the Daml runtime, as every project with
    /// generated bindings does. Others arent read at all, so a solution without Daml costs nothing.
    /// </summary>
    public static bool ReferencesDamlRuntime(Project project) =>
        (project ?? throw new ArgumentNullException(nameof(project))).MetadataReferences.OfType<PortableExecutableReference>()
            .Any(r => string.Equals(System.IO.Path.GetFileName(r.FilePath), "Daml.Runtime.dll", StringComparison.OrdinalIgnoreCase));

    /// <summary>The target framework of a project loaded once per framework; null for a project with one.</summary>
    public static string? TargetFramework(Project project) =>
        PerFramework.Match((project ?? throw new ArgumentNullException(nameof(project))).Name) is { Success: true } match ? match.Groups["framework"].Value : null;

    private static async Task<List<(string, string, IReadOnlyList<BindingLocation>)>> ReadAsync(IEnumerable<Document> documents, GenerationCatalog catalog, Action<string, string>? leftOut, CancellationToken cancellationToken)
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
            if (!ReferencesDamlRuntime(document.Project) || catalog.Find(path) is null || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                changes.Add((key, path, []));
                continue;
            }
            var framework = TargetFramework(document.Project);
            var read = BindingReader.Read(model, catalog, cancellationToken);
            if (read.Rejected.Count > 0 && await FromDiskAsync(document, path, cancellationToken).ConfigureAwait(false) is { } written)
            {
                read = BindingReader.Read(written, catalog, cancellationToken);
            }
            foreach (var problem in read.Rejected.Select(r => r.Problem).Distinct())
            {
                leftOut?.Invoke(path, problem);
            }
            changes.Add((key, path, read.Bindings.Select(b => new BindingLocation(b.Symbol, key, framework, b.Generation, b.DeclarationId, b.FilePath, b.Span.Start, b.Span.Length)).ToList()));
        }
        return changes;
    }

    // Visual Studio can go on holding the code of the build before, while the generation's folder,
    // swapped whole by the build, already has the new code and manifest; nothing would then match.
    // What the generator wrote is read instead, when the editor's copy differs from it.
    private static async Task<SemanticModel?> FromDiskAsync(Document document, string path, CancellationToken cancellationToken)
    {
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
