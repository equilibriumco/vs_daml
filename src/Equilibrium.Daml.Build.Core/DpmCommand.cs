using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.Build.Core;

/// <summary>A dpm action on a package or a multi-package folder.</summary>
public enum DpmCommandKind
{
    /// <summary><c>dpm build</c>: compile to a DAR.</summary>
    Build,

    /// <summary><c>dpm test</c>: run the package's scripts.</summary>
    Test,

    /// <summary>
    /// <c>dpm clean</c>: remove the package's own build output. It never touches dpm's shared
    /// cache of SDKs and components.
    /// </summary>
    Clean,
}

/// <summary>Arguments for dpm commands, kept apart from running them so both the IDE and MSBuild can use them.</summary>
public static class DpmCommand
{
    /// <summary>The dpm arguments for <paramref name="kind"/>.</summary>
    /// <param name="kind">What to run.</param>
    /// <param name="allPackages">
    /// Run on every package of the multi-package.yaml in the working folder (<c>--all</c>);
    /// otherwise only on the package in the working folder.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Testing all packages was asked for: <c>dpm test --all</c> means "also test dependencies",
    /// and testing runs one package at a time.
    /// </exception>
    public static IReadOnlyList<string> Arguments(DpmCommandKind kind, bool allPackages)
    {
        switch (kind)
        {
            case DpmCommandKind.Build:
                return allPackages ? ["build", "--all"] : ["build"];
            case DpmCommandKind.Clean:
                return allPackages ? ["clean", "--all"] : ["clean"];
            case DpmCommandKind.Test:
                if (allPackages)
                {
                    throw new ArgumentException("dpm tests one package at a time; run it in each package folder.", nameof(allPackages));
                }
                return ["test"];
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}
