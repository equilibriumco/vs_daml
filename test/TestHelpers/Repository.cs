using System;
using System.IO;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>Locations inside this repository, for tests that read the checked-in samples.</summary>
public static class Repository
{
    /// <summary>The repository root: the nearest folder above the test binaries with a <c>global.json</c>.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Full path of a folder or file under <c>samples/</c>.</summary>
    public static string Sample(string relativePath) => Path.Combine(Root, "samples", relativePath);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) && Directory.Exists(Path.Combine(dir.FullName, "samples")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"No repository root (global.json + samples) above {AppContext.BaseDirectory}.");
    }
}
