using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>
/// Whether a project's evaluation missed the list of its generated Daml files. The package names
/// the list's path in every evaluation, file or no file, and imports it only when it exists; Visual
/// Studio then watches only the files an evaluation imported. A list that exists now but isn't
/// among the imports was written after that evaluation, and IntelliSense won't see it until the
/// project is evaluated again.
/// </summary>
public static class GeneratedListImport
{
    /// <summary>Whether the list at <paramref name="listPath"/> exists but isn't among <paramref name="imports"/>.</summary>
    /// <param name="listPath">The list's path as the evaluation named it; empty for a project that names none.</param>
    /// <param name="imports">The full paths of the files the evaluation imported.</param>
    /// <param name="exists">Whether a file exists.</param>
    public static bool Missed(string? listPath, IEnumerable<string> imports, Func<string, bool> exists)
    {
        if (imports is null)
        {
            throw new ArgumentNullException(nameof(imports));
        }
        if (exists is null)
        {
            throw new ArgumentNullException(nameof(exists));
        }
        if (string.IsNullOrWhiteSpace(listPath) || !exists(listPath!))
        {
            return false;
        }
        var list = Normalize(listPath!);
        return !imports.Any(i => string.Equals(Normalize(i), list, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where the package keeps the list of a project's generated files for one configuration and
    /// target framework, <c>obj\daml\&lt;project file&gt;.&lt;configuration&gt;.&lt;framework&gt;.g.props</c>,
    /// from public properties: Visual Studio's evaluations leave out private ones, the package's own
    /// path among them. Null without a target framework, as in the outer evaluation of a project
    /// with several.
    /// </summary>
    /// <param name="projectDirectory">The project's folder.</param>
    /// <param name="baseIntermediateOutputPath"><c>BaseIntermediateOutputPath</c>, relative to the project or full.</param>
    /// <param name="projectFile"><c>MSBuildProjectFile</c>, the project's file name.</param>
    /// <param name="configuration"><c>Configuration</c>.</param>
    /// <param name="targetFramework"><c>TargetFramework</c>.</param>
    public static string? ListPath(string? projectDirectory, string? baseIntermediateOutputPath, string? projectFile, string? configuration, string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(baseIntermediateOutputPath) || string.IsNullOrWhiteSpace(projectFile)
            || string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }
        return Path.GetFullPath(Path.Combine(projectDirectory!, baseIntermediateOutputPath!, "daml", $"{projectFile}.{configuration}.{targetFramework}.g.props"));
    }

    private static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>What to do about a list an evaluation missed.</summary>
public enum MissedListAction
{
    /// <summary>Nothing: the list was seen, or a reload was asked for already.</summary>
    None,

    /// <summary>Have the project evaluated again.</summary>
    EvaluateAgain,

    /// <summary>Evaluating again didn't help: ask for the project to be reloaded.</summary>
    AskForReload,
}

/// <summary>
/// Keeps track of the lists evaluations missed, so a project is evaluated again once, and if that
/// doesn't help (Visual Studio may go on holding what it found missing, as after a package cache
/// emptied under it) a reload is asked for once, rather than after every build. A list that is seen
/// again is forgotten. Thread-safe.
/// </summary>
public sealed class MissedLists
{
    private readonly Dictionary<string, MissedListAction> _done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What to do now that the evaluation did or didn't miss <paramref name="list"/>.</summary>
    public MissedListAction Next(string list, bool missed)
    {
        if (list is null)
        {
            throw new ArgumentNullException(nameof(list));
        }
        lock (_done)
        {
            if (!missed)
            {
                _done.Remove(list);
                return MissedListAction.None;
            }
            var next = _done.TryGetValue(list, out var done)
                ? done == MissedListAction.EvaluateAgain ? MissedListAction.AskForReload : MissedListAction.None
                : MissedListAction.EvaluateAgain;
            if (next != MissedListAction.None)
            {
                _done[list] = next;
            }
            return next;
        }
    }
}
