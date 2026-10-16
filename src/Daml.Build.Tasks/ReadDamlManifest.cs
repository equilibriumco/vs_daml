using System;
using System.IO;
using System.Linq;
using Daml.Build.Core.Diagnostics;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Manifests;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

/// <summary>
/// Reads a package's <c>daml.yaml</c> for the build: what the package is called, where its
/// sources are, what it depends on and where its DAR goes. It only reads files, so design-time
/// builds may run it. Every problem is a DAML0100 error at its place in the manifest.
/// </summary>
public sealed class ReadDamlManifest : Task
{
    private readonly IDamlFileSystem _files;

    /// <summary>Creates the task over the real file system.</summary>
    public ReadDamlManifest()
        : this(new PhysicalFileSystem())
    {
    }

    internal ReadDamlManifest(IDamlFileSystem files) => _files = files;

    /// <summary>The <c>daml.yaml</c>; a relative path is relative to the project.</summary>
    [Required]
    public string ManifestPath { get; set; } = "";

    /// <summary><c>name</c>.</summary>
    [Output]
    public string? PackageName { get; private set; }

    /// <summary><c>version</c>.</summary>
    [Output]
    public string? PackageVersion { get; private set; }

    /// <summary><c>sdk-version</c>; empty when the manifest leaves it to dpm.</summary>
    [Output]
    public string? SdkVersion { get; private set; }

    /// <summary>The folder dpm builds in.</summary>
    [Output]
    public string? PackageRoot { get; private set; }

    /// <summary>The full path of <c>source</c>.</summary>
    [Output]
    public string? SourcePath { get; private set; }

    /// <summary><c>Folder</c> or <c>File</c>.</summary>
    [Output]
    public string? SourceKind { get; private set; }

    /// <summary>Where the build writes the DAR.</summary>
    [Output]
    public string? DarPath { get; private set; }

    /// <summary><c>components</c>, as written.</summary>
    [Output]
    public ITaskItem[] Components { get; private set; } = [];

    /// <summary><c>dependencies</c> as written; a DAR entry has its full path in <c>DarPath</c> metadata.</summary>
    [Output]
    public ITaskItem[] Dependencies { get; private set; } = [];

    /// <summary><c>data-dependencies</c> as full DAR paths, each with the entry as written in <c>Written</c> metadata.</summary>
    [Output]
    public ITaskItem[] DataDependencies { get; private set; } = [];

    /// <inheritdoc />
    public override bool Execute()
    {
        // MSBuild runs tasks with the project's folder as the current one.
        var path = Path.GetFullPath(ManifestPath);
        var result = DamlPackageBuild.Read(new YamlManifestReader(_files), _files, path);
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity == ManifestSeverity.Error)
            {
                Log.LogError(null, DamlDiagnosticCodes.InvalidConfiguration, null, diagnostic.Path, diagnostic.Line ?? 0, diagnostic.Column ?? 0, 0, 0, diagnostic.Message);
            }
            else
            {
                Log.LogWarning(null, DamlDiagnosticCodes.InvalidConfiguration, null, diagnostic.Path, diagnostic.Line ?? 0, diagnostic.Column ?? 0, 0, 0, diagnostic.Message);
            }
        }
        if (result.Value is not { } package)
        {
            return false;
        }

        PackageName = package.Name;
        PackageVersion = package.Version;
        SdkVersion = package.SdkVersion ?? "";
        PackageRoot = package.PackageRoot;
        SourcePath = package.Source.Path;
        SourceKind = package.Source.Kind.ToString();
        DarPath = package.DarPath;
        Components = package.Components.Select(c => (ITaskItem)new TaskItem(c)).ToArray();
        Dependencies = package.Dependencies.Select(d => (ITaskItem)Item(d.Text, "DarPath", d.DarPath)).ToArray();
        DataDependencies = package.DataDependencies.Select(d => (ITaskItem)Item(d.DarPath ?? d.Text, "Written", d.Text)).ToArray();
        return !Log.HasLoggedErrors;
    }

    private static TaskItem Item(string spec, string metadata, string? value)
    {
        var item = new TaskItem(spec);
        if (value is not null)
        {
            item.SetMetadata(metadata, value);
        }
        return item;
    }
}
