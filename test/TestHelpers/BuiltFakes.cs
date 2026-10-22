using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Equilibrium.Daml.TestHelpers;

// Build output folders of the fake executables, baked into this assembly by TestHelpers.csproj.
internal static class BuiltFakes
{
    public static string Executable(string metadataKey, string fileName)
    {
        var directory = typeof(BuiltFakes).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == metadataKey)?.Value;
        var path = directory is null ? null : Path.Combine(directory, fileName);
        if (path is null || !File.Exists(path))
        {
            throw new InvalidOperationException($"{fileName} isn't built at '{directory}'. Build the solution first.");
        }
        return path;
    }
}
