using System;
using System.Collections.Generic;
using System.Linq;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>
/// What has to be read again into a <see cref="BindingIndex"/>: everything, whole projects,
/// single documents, and generations whose manifests may have changed. Merging keeps the least
/// that covers both: reading everything covers projects and documents, reading a project covers
/// its documents, and of adding and removing a project the later one wins.
/// </summary>
public sealed class IndexChanges
{
    private static readonly IReadOnlyCollection<(string, string)> NoDocuments = [];

    private IndexChanges(bool all, IReadOnlyCollection<string> projects, IReadOnlyCollection<string> removedProjects, IReadOnlyCollection<(string, string)> documents, IReadOnlyCollection<string> generations)
    {
        All = all;
        Projects = projects;
        RemovedProjects = removedProjects;
        Documents = documents;
        Generations = generations;
    }

    /// <summary>Nothing to read.</summary>
    public static IndexChanges None { get; } = new(false, [], [], NoDocuments, []);

    /// <summary>The whole solution, as when it was opened or reloaded.</summary>
    public static IndexChanges Everything { get; } = new(true, [], [], NoDocuments, []);

    /// <summary>A project added, reloaded or changed (its references, say).</summary>
    public static IndexChanges Project(string projectKey) => new(false, [projectKey], [], NoDocuments, []);

    /// <summary>A project gone from the solution.</summary>
    public static IndexChanges RemovedProject(string projectKey) => new(false, [], [projectKey], NoDocuments, []);

    /// <summary>A document of a project added, changed or removed.</summary>
    public static IndexChanges Document(string projectKey, string filePath) => new(false, [], [], [(projectKey, filePath)], []);

    /// <summary>A generation's folder, whose manifest may have been written again.</summary>
    public static IndexChanges Generation(string folder) => new(false, [], [], NoDocuments, [folder]);

    /// <summary>Whether everything is to be read.</summary>
    public bool All { get; }

    /// <summary>Projects to read again whole.</summary>
    public IReadOnlyCollection<string> Projects { get; }

    /// <summary>Projects to drop.</summary>
    public IReadOnlyCollection<string> RemovedProjects { get; }

    /// <summary>Documents to read again, by project and file.</summary>
    public IReadOnlyCollection<(string ProjectKey, string FilePath)> Documents { get; }

    /// <summary>Generation folders to read again, manifest first.</summary>
    public IReadOnlyCollection<string> Generations { get; }

    /// <summary>Whether there is nothing to read.</summary>
    public bool IsEmpty => !All && Projects.Count == 0 && RemovedProjects.Count == 0 && Documents.Count == 0 && Generations.Count == 0;

    /// <summary>These changes followed by <paramref name="later"/>.</summary>
    public IndexChanges Merge(IndexChanges later)
    {
        if (later is null)
        {
            throw new ArgumentNullException(nameof(later));
        }
        var generations = new HashSet<string>(Generations.Concat(later.Generations), StringComparer.OrdinalIgnoreCase);
        if (All || later.All)
        {
            return new IndexChanges(true, [], [], NoDocuments, generations.ToList());
        }
        var projects = new HashSet<string>(Projects, StringComparer.Ordinal);
        var removed = new HashSet<string>(RemovedProjects, StringComparer.Ordinal);
        foreach (var project in later.RemovedProjects)
        {
            projects.Remove(project);
            removed.Add(project);
        }
        foreach (var project in later.Projects)
        {
            removed.Remove(project);
            projects.Add(project);
        }
        var documents = new HashSet<(string, string)>(new DocumentComparer());
        foreach (var document in Documents.Concat(later.Documents))
        {
            if (!projects.Contains(document.Item1) && !removed.Contains(document.Item1))
            {
                documents.Add(document);
            }
        }
        return new IndexChanges(false, projects.ToList(), removed.ToList(), documents.ToList(), generations.ToList());
    }

    private sealed class DocumentComparer : IEqualityComparer<(string Project, string File)>
    {
        public bool Equals((string Project, string File) x, (string Project, string File) y) =>
            string.Equals(x.Project, y.Project, StringComparison.Ordinal) && string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Project, string File) key) =>
            StringComparer.Ordinal.GetHashCode(key.Project) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(key.File);
    }
}
