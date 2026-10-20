using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

namespace Daml.Build.Core.Building;

/// <summary>Leases a private compiler cache exclusively for one command, retaining metadata between commands.</summary>
public sealed class CompilerCache : IDisposable
{
    internal const int Slots = 8;
    private readonly string _directory;
    private readonly FileStream? _lease;

    /// <summary>Uses a bounded cache pool; overflow commands get temporary caches without waiting.</summary>
    public CompilerCache() : this(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DamlVisualStudio", "compiler-cache")) { }

    internal CompilerCache(string root)
    {
        (_directory, _lease) = Acquire(root);
        Environment = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?> { ["DAML_CACHE"] = _directory });
    }

    /// <summary>Environment overrides for the child compiler. DPM's installed components are unaffected.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>The DAML_CACHE assignment accepted by MSBuild ToolTask.</summary>
    public string Assignment => "DAML_CACHE=" + _directory;

    private static (string Directory, FileStream? Lease) Acquire(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            for (var slot = 0; slot < Slots; slot++)
            {
                var path = Path.Combine(root, slot.ToString(CultureInfo.InvariantCulture));
                try
                {
                    return (path, new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException) { } // The slot is held or unavailable; try another.
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (Path.Combine(Path.GetTempPath(), "daml-vs-compiler-" + Guid.NewGuid().ToString("N")), null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_lease is not null)
        {
            _lease.Dispose();
            return;
        }
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
