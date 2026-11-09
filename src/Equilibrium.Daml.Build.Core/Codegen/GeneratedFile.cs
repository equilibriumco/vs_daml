using System;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>A file the generator owns, to be compiled.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="GenerationId">The generation it belongs to.</param>
public sealed record GeneratedFile(string Path, string GenerationId)
{
    /// <summary>
    /// Whether a file the generator wrote goes to the compiler. The generator also writes files of
    /// its own (a listing of its output, for one); a generation owns them, but they aren't C#.
    /// </summary>
    public static bool IsCompiled(string path) =>
        (path ?? throw new ArgumentNullException(nameof(path))).EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
