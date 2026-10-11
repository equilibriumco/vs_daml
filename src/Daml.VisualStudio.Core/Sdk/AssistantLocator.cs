using System;
using System.Collections.Generic;
using System.IO;
using Daml.VisualStudio.Core.FileSystem;

namespace Daml.VisualStudio.Core.Sdk;

/// <summary>
/// Finds dpm or the daml assistant: a configured path if there is one, else the first PATH
/// entry that has the tool, else the folder its installer uses.
/// </summary>
public sealed class AssistantLocator
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    private readonly IDamlFileSystem _fileSystem;
    private readonly Func<string, string?> _environment;

    /// <summary>Creates a locator over the real file system and process environment.</summary>
    public AssistantLocator()
        : this(new PhysicalFileSystem(), Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Creates a locator reading files and environment variables through the given accessors.</summary>
    public AssistantLocator(IDamlFileSystem fileSystem, Func<string, string?> environment)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>Finds <paramref name="kind"/>, or returns <c>null</c> if it isn't installed.</summary>
    /// <param name="kind">Tool to look for.</param>
    /// <param name="configuredPath">Path set in the options; empty means search.</param>
    /// <exception cref="SdkResolutionException">The configured path doesn't exist.</exception>
    public AssistantLocation? Find(AssistantKind kind, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // A typo in the options must not quietly fall back to some other installation.
            var path = DamlPath.Normalize(configuredPath!);
            return _fileSystem.FileExists(path)
                ? new AssistantLocation(kind, path)
                : throw new SdkResolutionException($"The {ToolName(kind)} path set in Tools > Options > Daml doesn't exist: {configuredPath}");
        }

        var name = kind == AssistantKind.Dpm ? "dpm" : "daml";
        foreach (var directory in SearchDirectories(kind))
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (_fileSystem.FileExists(candidate))
                {
                    return new AssistantLocation(kind, candidate);
                }
            }
        }
        return null;
    }

    internal static string ToolName(AssistantKind kind) => kind == AssistantKind.Dpm ? "dpm" : "daml assistant";

    private IEnumerable<string> SearchDirectories(AssistantKind kind)
    {
        foreach (var entry in (_environment("PATH") ?? "").Split(';'))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length > 0 && Path.IsPathRooted(directory))
            {
                yield return directory;
            }
        }

        var installRoot = kind == AssistantKind.Dpm ? _environment("APPDATA") : _environment("USERPROFILE");
        if (!string.IsNullOrEmpty(installRoot))
        {
            yield return kind == AssistantKind.Dpm
                ? Path.Combine(installRoot, @"dpm\bin")
                : Path.Combine(installRoot, @".daml\bin");
        }
    }
}
