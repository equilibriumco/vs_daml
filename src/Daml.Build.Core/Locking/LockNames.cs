using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Locking;

/// <summary>
/// Where the locks of the Daml build live: per user, so builds of different users never wait on
/// each other, and named after what they guard.
/// </summary>
public static class LockNames
{
    /// <summary><c>%LOCALAPPDATA%\DamlVisualStudio\locks</c>.</summary>
    public static string Root(Func<string, string?> environment)
    {
        if (environment is null)
        {
            throw new ArgumentNullException(nameof(environment));
        }
        var localAppData = environment("LOCALAPPDATA");
        return string.IsNullOrEmpty(localAppData)
            ? throw new InvalidOperationException("LOCALAPPDATA isn't set, so there is nowhere to keep the build's locks.")
            : Path.Combine(localAppData!, "DamlVisualStudio", "locks");
    }

    /// <summary>The lock on the private cache of the generator with <paramref name="digest"/>.</summary>
    public static string Generator(string root, string digest) => Path.Combine(root, $"codegen-{digest}.lock");

    /// <summary>
    /// The lock on a generation root: one per canonical folder in any spelling, so two builds
    /// writing the same folder always share it.
    /// </summary>
    public static string Output(string root, string folder)
    {
        var canonical = DamlPath.Normalize(folder).ToUpperInvariant();
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        }
        return Path.Combine(root, "output-" + string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))) + ".lock");
    }
}
