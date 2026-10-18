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

    private static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
