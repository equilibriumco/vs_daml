using System;
using System.IO;
using System.Text.RegularExpressions;
using Daml.TestHelpers;

namespace Daml.VisualStudio.LanguageServer.Tests.Contract;

// A throw-away copy of a sample pinned to one SDK, so contract runs never write build output
// into the repository and the same sources run on every SDK of the matrix.
internal sealed class SampleCopy : IDisposable
{
    private SampleCopy(string root, string sdkVersion)
    {
        Root = root;
        SdkVersion = sdkVersion;
    }

    public string Root { get; }

    public string SdkVersion { get; }

    public static SampleCopy Create(string sample, string sdkVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "daml-contract", $"{sample}-{sdkVersion}-{Guid.NewGuid().ToString("N").Substring(0, 8)}");
        Copy(Repository.Sample(sample), root, sdkVersion);
        return new SampleCopy(root, sdkVersion);
    }

    public string File(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // damlc may still hold a file for a moment; the temp folder is cleaned up eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Copy(string from, string to, string sdkVersion)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            var name = Path.GetFileName(file);
            if (name.Equals("daml.yaml", StringComparison.OrdinalIgnoreCase) || name.Equals("multi-package.yaml", StringComparison.OrdinalIgnoreCase))
            {
                var text = System.IO.File.ReadAllText(file);
                System.IO.File.WriteAllText(target, Regex.Replace(text, @"(?m)^sdk-version:\s*\S+", "sdk-version: " + sdkVersion));
            }
            else
            {
                System.IO.File.Copy(file, target);
            }
        }
        foreach (var directory in Directory.GetDirectories(from))
        {
            // Build output belongs to the SDK that made it.
            if (!Path.GetFileName(directory).Equals(".daml", StringComparison.OrdinalIgnoreCase))
            {
                Copy(directory, Path.Combine(to, Path.GetFileName(directory)), sdkVersion);
            }
        }
    }
}
