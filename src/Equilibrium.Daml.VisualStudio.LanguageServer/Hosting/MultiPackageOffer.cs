using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.VisualStudio.Core.Project;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;

/// <summary>
/// The offer of a multi-package.yaml for a solution's Daml packages, as the user meets it: made
/// once a session for a given set of packages, never again once declined for the solution, and
/// never writing over a file that is already there.
/// </summary>
public sealed class MultiPackageOffer
{
    /// <summary>The file under <c>.vs\EquilibriumDamlTools</c> that says the user declined the offer for the solution.</summary>
    public const string DismissalFileName = "multi-package-offer.declined";

    private readonly IDamlFileSystem _fileSystem;
    private readonly MultiPackageAdvisor _advisor;
    private readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declined = new(DamlPath.Comparer);
    private readonly object _gate = new();

    /// <summary>Creates the offer, reading manifests through <paramref name="reader"/>.</summary>
    public MultiPackageOffer(IDamlFileSystem fileSystem, IManifestReader reader)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _advisor = new MultiPackageAdvisor(fileSystem, reader ?? throw new ArgumentNullException(nameof(reader)));
    }

    /// <summary>
    /// What to tell the user about <paramref name="solutionDirectory"/>, or <c>null</c>: nothing is
    /// missing, the user declined, or this was said already in this session.
    /// </summary>
    /// <param name="solutionDirectory">The solution's folder.</param>
    /// <param name="manifests">The daml.yaml of each Daml project in the solution.</param>
    public MultiPackageProposal? ToShow(string solutionDirectory, IReadOnlyCollection<string> manifests)
    {
        lock (_gate)
        {
            if (_declined.Contains(DamlPath.Normalize(solutionDirectory)))
            {
                return null;
            }
        }
        if (_fileSystem.FileExists(DismissalPath(solutionDirectory)))
        {
            return null;
        }
        var proposal = _advisor.Advise(solutionDirectory, manifests);
        if (proposal.Kind == MultiPackageAdvice.None)
        {
            return null;
        }
        var key = $"{proposal.Kind}|{proposal.ManifestPath}|{string.Join("|", proposal.Packages.OrderBy(p => p, DamlPath.Comparer))}";
        lock (_gate)
        {
            return _shown.Add(key) ? proposal : null;
        }
    }

    /// <summary>
    /// Writes the offered multi-package.yaml; <c>false</c> when a file of that name appeared in the
    /// meantime, which is left as it is.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder can't be written to.</exception>
    public bool Create(string solutionDirectory, MultiPackageProposal proposal)
    {
        if (proposal is null)
        {
            throw new ArgumentNullException(nameof(proposal));
        }
        if (_fileSystem.FileExists(proposal.ManifestPath))
        {
            return false;
        }
        var content = Encoding.UTF8.GetBytes(MultiPackageAdvisor.Content(solutionDirectory, proposal.Packages));
        // Straight to disk, not through the file system the reads use: the file may only be
        // created where none is, in one step, so a file the user made meanwhile is never replaced.
        try
        {
            using var file = new FileStream(proposal.ManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(content, 0, content.Length);
            return true;
        }
        catch (IOException) when (File.Exists(proposal.ManifestPath))
        {
            return false;
        }
    }

    /// <summary>
    /// Remembers that the user doesn't want to be asked again: at once for this session, and in the
    /// solution's .vs folder for later ones.
    /// </summary>
    /// <exception cref="IOException">The marker couldn't be written; this session still won't ask.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder can't be written to; this session still won't ask.</exception>
    public void Dismiss(string solutionDirectory)
    {
        lock (_gate)
        {
            _declined.Add(DamlPath.Normalize(solutionDirectory));
        }
        var path = DismissalPath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "EQ Daml Tools doesn't offer a multi-package.yaml for this solution any more. Delete this file to be asked again.\n");
    }

    private static string DismissalPath(string solutionDirectory) =>
        Path.Combine(DamlPath.Normalize(solutionDirectory), ".vs", "EquilibriumDamlTools", DismissalFileName);
}
