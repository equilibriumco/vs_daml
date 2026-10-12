using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Logging;

namespace Daml.VisualStudio.LanguageServer.Messages;

/// <summary>Shows a running SDK installation to the user.</summary>
public interface ISdkInstallPresenter
{
    /// <summary>Starts showing an installation; <paramref name="cancel"/> runs when the user cancels it.</summary>
    ISdkInstallView Show(string title, Func<Task> cancel);
}

/// <summary>One installation on screen.</summary>
public interface ISdkInstallView
{
    /// <summary>Updates the percentage, 0 to 100.</summary>
    void Report(int percentComplete);

    /// <summary>The installation is over.</summary>
    void Complete();
}

/// <summary>
/// Follows the server's SDK installations from begin to end, one view per SDK, and turns the
/// user's cancel into a <c>daml/sdkInstallCancel</c> request.
/// </summary>
public sealed class SdkInstallProgressTracker : ISdkInstallProgressSink
{
    private readonly ISdkInstallPresenter _presenter;
    private readonly Func<string, Task<bool>> _cancelInstall;
    private readonly IDamlLogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ISdkInstallView> _running = new(StringComparer.Ordinal);

    /// <summary>Creates the tracker.</summary>
    /// <param name="presenter">Shows installations.</param>
    /// <param name="cancelInstall">Asks the server to cancel an installation; false when there is no server to ask.</param>
    /// <param name="logger">Where installations are logged.</param>
    public SdkInstallProgressTracker(ISdkInstallPresenter presenter, Func<string, Task<bool>> cancelInstall, IDamlLogger logger)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _cancelInstall = cancelInstall ?? throw new ArgumentNullException(nameof(cancelInstall));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Report(SdkInstallProgressParams progress)
    {
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }
        var percent = Math.Max(0, Math.Min(100, progress.Progress));
        ISdkInstallView? view;
        lock (_gate)
        {
            _running.TryGetValue(progress.SdkVersionIdentifier, out view);
            if (progress.Kind == SdkInstallProgressKind.End)
            {
                _running.Remove(progress.SdkVersionIdentifier);
            }
            else if (view is null)
            {
                // A report without a begin still means an installation is running.
                var id = progress.SdkVersionIdentifier;
                view = _presenter.Show($"Installing Daml {progress.SdkVersionRendered}", () => CancelAsync(id));
                _running[id] = view;
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"Installing Daml {progress.SdkVersionRendered}.");
            }
        }

        if (progress.Kind == SdkInstallProgressKind.End)
        {
            if (view is not null)
            {
                view.Complete();
                _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"Installation of Daml {progress.SdkVersionRendered} finished.");
            }
            return;
        }
        view!.Report(percent);
    }

    private async Task CancelAsync(string sdkVersionIdentifier)
    {
        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"Cancelling the installation of SDK {sdkVersionIdentifier}.");
        if (!await _cancelInstall(sdkVersionIdentifier).ConfigureAwait(false))
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"The installation of SDK {sdkVersionIdentifier} couldn't be cancelled: the language server isn't connected.");
        }
    }
}
