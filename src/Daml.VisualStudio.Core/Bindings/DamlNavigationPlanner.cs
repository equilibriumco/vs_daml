using System;
using System.Collections.Generic;
using System.Linq;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>What going to a Daml definition does.</summary>
public enum DamlNavigationKind
{
    /// <summary>Opens the one current source.</summary>
    Go,

    /// <summary>Lets the user choose among several current sources.</summary>
    Choose,

    /// <summary>Explains that the sources changed after the C# was generated, and offers a build.</summary>
    Stale,

    /// <summary>Explains that there are no sources on this machine.</summary>
    External,
}

/// <summary>A symbol in one of its sources.</summary>
/// <param name="Symbol">The Daml symbol.</param>
/// <param name="Source">The package with its sources.</param>
public sealed record DamlNavigationTarget(DamlSymbolId Symbol, SourceCandidate Source);

/// <summary>The decision, the places it is about, and what to tell the user.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Targets">Where to go, or what to choose from, or what is stale.</param>
/// <param name="Message">What to tell the user; empty when going straight there.</param>
public sealed record DamlNavigationPlan(DamlNavigationKind Kind, IReadOnlyList<DamlNavigationTarget> Targets, string Message);

/// <summary>
/// Decides how to get from C# to Daml given the local sources of the symbols at hand: straight to
/// the one current source; a choice among several, never the first; an explanation when the only
/// sources changed after the build or when there are none.
/// </summary>
public static class DamlNavigationPlanner
{
    /// <summary>The plan for <paramref name="found"/>: each symbol with its source candidates.</summary>
    public static DamlNavigationPlan Plan(IReadOnlyList<(DamlSymbolId Symbol, IReadOnlyList<SourceCandidate> Sources)> found)
    {
        if (found is null || found.Count == 0)
        {
            throw new ArgumentException("There has to be a symbol to go to.", nameof(found));
        }
        var targets = found.SelectMany(f => f.Sources.Select(s => new DamlNavigationTarget(f.Symbol, s))).ToList();
        var current = targets.Where(t => t.Source.Status == SourceStatus.Current).ToList();
        var names = string.Join(", ", found.Select(f => Name(f.Symbol)).Distinct());
        if (current.Count == 1)
        {
            return new DamlNavigationPlan(DamlNavigationKind.Go, current, "");
        }
        if (current.Count > 1)
        {
            return new DamlNavigationPlan(DamlNavigationKind.Choose, current, $"{names} has sources in several Daml packages; choose one.");
        }
        if (targets.Count > 0)
        {
            return new DamlNavigationPlan(
                DamlNavigationKind.Stale,
                targets,
                $"The Daml sources of {names} changed after its C# was generated, so they no longer show where it is. Build the solution to bring the bindings up to date.");
        }
        return new DamlNavigationPlan(
            DamlNavigationKind.External,
            [],
            $"{names} comes from package {found[0].Symbol.PackageId}, whose Daml sources aren't on this machine: it was generated from a DAR.");
    }

    /// <summary><c>Kinds.Asset:Asset.Give</c>: the symbol as Daml code names it.</summary>
    public static string Name(DamlSymbolId symbol) =>
        $"{(symbol ?? throw new ArgumentNullException(nameof(symbol))).Module}:{symbol.Entity}{(symbol.Member is null ? "" : "." + symbol.Member)}";
}
