using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Manifests;
using Daml.Build.Core.Processes;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Server;

namespace Daml.VisualStudio.Core.Sdk;

/// <summary>
/// Picks the tool that runs a workspace's language server. dpm is preferred; the daml assistant
/// is used when the server's working folder pins an SDK older than 3.4, or when it's the only
/// tool installed and nothing in that folder says it can't run the SDK.
/// </summary>
public sealed class SdkResolver
{
    private const int FirstDpmMajor = 3;
    private const int FirstDpmMinor = 4;

    private readonly AssistantLocator _locator;
    private readonly IManifestReader _manifests;
    private readonly ISdkResolver _dpm;
    private readonly ISdkResolver _damlAssistant;
    private readonly DamlcProbe _probe;

    /// <summary>Creates a resolver with the standard dpm and daml assistant strategies.</summary>
    public SdkResolver(IProcessRunner runner, AssistantLocator locator, IManifestReader manifests)
        : this(locator, manifests, new DpmSdkResolver(runner), new DamlAssistantSdkResolver(runner), new DamlcProbe(runner))
    {
    }

    /// <summary>Creates a resolver with explicit strategies.</summary>
    public SdkResolver(AssistantLocator locator, IManifestReader manifests, ISdkResolver dpm, ISdkResolver damlAssistant, DamlcProbe probe)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _dpm = dpm ?? throw new ArgumentNullException(nameof(dpm));
        _damlAssistant = damlAssistant ?? throw new ArgumentNullException(nameof(damlAssistant));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// Resolves the tool and SDK versions for <paramref name="workspace"/>. Manifests are read
    /// and tools are run on worker threads, so the caller's thread is never blocked.
    /// </summary>
    /// <exception cref="SdkResolutionException">No installed tool can run the workspace.</exception>
    public async Task<SdkResolution> ResolveAsync(WorkspaceContext workspace, LanguageServerOptions options, CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var notes = new List<string>();
        var workingDirectory = workspace.ServerWorkingDirectory;
        var (packages, contextSdk) = await Task.Run(() => (ReadPackages(workspace, notes), ReadContextSdk(workingDirectory)), cancellationToken).ConfigureAwait(false);
        var predatesDpm = contextSdk is null ? null : SdkVersion.IsBefore(contextSdk, FirstDpmMajor, FirstDpmMinor);
        if (contextSdk is not null && predatesDpm is null)
        {
            notes.Add($"SDK version {contextSdk} doesn't say which features it has; the language server's capabilities will be checked instead of assuming the latest.");
        }

        var dpm = _locator.Find(AssistantKind.Dpm, options.DpmPath);
        var damlAssistant = _locator.Find(AssistantKind.DamlAssistant, options.DamlAssistantPath);
        var activeDpmVersion = dpm is null ? null : await TryReadAsync(_dpm, dpm, workingDirectory, notes, cancellationToken).ConfigureAwait(false);

        AssistantLocation assistant;
        string? assistantSdk = null;
        if (predatesDpm == true || (dpm is null && damlAssistant is not null))
        {
            if (damlAssistant is null)
            {
                throw new SdkResolutionException(
                    $"The workspace uses SDK {contextSdk}, which predates dpm (SDK 3.4) and needs the daml assistant. Install it, or set its path in Tools > Options > Daml.");
            }
            if (predatesDpm == false)
            {
                throw new SdkResolutionException(
                    $"The workspace uses SDK {contextSdk}, which is run by dpm, and dpm wasn't found on PATH or in %APPDATA%\\dpm\\bin. Install dpm, or set its path in Tools > Options > Daml.");
            }
            assistant = damlAssistant;
            assistantSdk = await TryReadAsync(_damlAssistant, damlAssistant, workingDirectory, notes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            assistant = dpm ?? throw new SdkResolutionException(
                "Neither dpm nor the daml assistant was found on PATH, in %APPDATA%\\dpm\\bin or in %USERPROFILE%\\.daml\\bin. " +
                "Install dpm (Digital Asset's package manager), or set its path in Tools > Options > Daml.");
        }

        var capabilities = await TryProbeAsync(assistant, workingDirectory, notes, cancellationToken).ConfigureAwait(false);
        // Without an answer from damlc, the SDK version is only a hint: multi-ide arrived in 2.9.
        var hintVersion = contextSdk ?? (assistant.Kind == AssistantKind.Dpm ? activeDpmVersion : assistantSdk);
        var multiIdeHint = hintVersion is null ? null : !SdkVersion.IsBefore(hintVersion, 2, 9);

        var pinned = SdkResolution.PinnedSdkVersions(packages);
        if (pinned.Count > 1)
        {
            notes.Add($"Packages pin different SDK versions ({string.Join(", ", pinned)}); each package is compiled with its own.");
        }

        return new SdkResolution
        {
            Assistant = assistant,
            ContextSdkVersion = contextSdk,
            ActiveDpmVersion = activeDpmVersion,
            AssistantSdkVersion = assistantSdk,
            CoordinatorVersion = capabilities?.Version,
            SupportsMultiIde = capabilities?.SupportsMultiIde ?? multiIdeHint,
            SupportsIdeIdentifier = capabilities?.SupportsIdeIdentifier,
            Packages = packages,
            Notes = notes,
        };
    }

    // In multi-package mode the server's working directory usually has no daml.yaml of its own.
    private string? ReadContextSdk(string workingDirectory)
    {
        var manifestPath = Path.Combine(workingDirectory, WorkspaceLocator.PackageManifestName);
        return _manifests.Exists(manifestPath) ? _manifests.ReadPackage(manifestPath).Value?.SdkVersion : null;
    }

    private List<PackageSdk> ReadPackages(WorkspaceContext workspace, List<string> notes)
    {
        var packages = new List<PackageSdk>();
        foreach (var root in workspace.Packages)
        {
            var manifestPath = Path.Combine(root, WorkspaceLocator.PackageManifestName);
            if (_manifests.Exists(manifestPath) && _manifests.ReadPackage(manifestPath).Value is { } manifest)
            {
                packages.Add(new PackageSdk(root, manifest.Name, manifest.SdkVersion, manifest.Components));
            }
            else
            {
                notes.Add($"Can't read {manifestPath}; its SDK version is unknown.");
            }
        }
        return packages;
    }

    private async Task<DamlcCapabilities?> TryProbeAsync(AssistantLocation assistant, string workingDirectory, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ProbeAsync(assistant, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or TimeoutException or IOException or Win32Exception)
        {
            notes.Add($"Couldn't ask damlc what it supports, so the SDK version is used as a guess: {ex.Message}");
            return null;
        }
    }

    // The version only informs; a tool that can't report it may still run the server.
    private static async Task<string?> TryReadAsync(ISdkResolver resolver, AssistantLocation assistant, string workingDirectory, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            return await resolver.ReadSdkVersionAsync(assistant, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or TimeoutException or IOException or Win32Exception)
        {
            notes.Add($"Couldn't read the {AssistantLocator.ToolName(assistant.Kind)} SDK version: {ex.Message}");
            return null;
        }
    }
}
