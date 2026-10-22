using System;
using System.Collections.Generic;
using System.IO;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.FileSystem;
using Equilibrium.Daml.Build.Core.Tools;

namespace Equilibrium.Daml.VisualStudio.Core.Sdk;

/// <summary>
/// Finds dpm or the daml assistant: a configured path if there is one, else the first PATH
/// entry that has the tool, else the folder its installer uses.
/// </summary>
public sealed class AssistantLocator
{
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
    /// <exception cref="SdkResolutionException">The configured path doesn't exist or isn't a full path.</exception>
    public AssistantLocation? Find(AssistantKind kind, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // A typo in the options must not quietly fall back to some other installation.
            var path = ConfiguredPath(kind, configuredPath!);
            return _fileSystem.FileExists(path)
                ? new AssistantLocation(kind, path)
                : throw new SdkResolutionException($"The {ToolName(kind)} path set in Tools > Options > Daml doesn't exist: {configuredPath}");
        }

        var found = kind == AssistantKind.Dpm
            ? DpmLocation.Find(null, "", _environment, _fileSystem, new List<ConfigurationError>())
            : ToolSearch.Find("daml", _environment("PATH"), InstallFolder(), _fileSystem);
        return found is null ? null : new AssistantLocation(kind, found);
    }

    internal static string ToolName(AssistantKind kind) => kind == AssistantKind.Dpm ? "dpm" : "daml assistant";

    // Pasted from a file dialog it may carry quotes; typed by hand it may not be a path at all.
    private static string ConfiguredPath(AssistantKind kind, string configured)
    {
        try
        {
            return DamlPath.Normalize(configured.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SdkResolutionException($"The {ToolName(kind)} path set in Tools > Options > Daml isn't a full path to a file: {configured}");
        }
    }

    private string? InstallFolder()
    {
        var installRoot = _environment("USERPROFILE");
        if (string.IsNullOrEmpty(installRoot))
        {
            return null;
        }
        return Path.Combine(installRoot, @".daml\bin");
    }
}
