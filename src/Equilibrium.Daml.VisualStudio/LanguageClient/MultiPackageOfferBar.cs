using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.LanguageClient;

/// <summary>
/// The info bar that offers a multi-package.yaml for the solution's Daml packages, or points out
/// what the one in the solution folder misses. Creating the file goes through the same restart as
/// any multi-package.yaml change, so the server runs in the solution folder afterwards.
/// </summary>
internal sealed class MultiPackageOfferBar
{
    private const string CreateAction = "Create multi-package.yaml for cross-package navigation";
    private const string OpenAction = "Open multi-package.yaml";
    private const string DeclineAction = "Don't ask again for this solution";

    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly IDamlLogger _logger;
    private readonly LanguageServerHost _host;
    private readonly MultiPackageOffer _offer;

    public MultiPackageOfferBar(JoinableTaskFactory joinableTaskFactory, IDamlLogger logger, LanguageServerHost host, MultiPackageOffer offer)
    {
        _joinableTaskFactory = joinableTaskFactory;
        _logger = logger;
        _host = host;
        _offer = offer;
    }

    public void Show(string solutionDirectory, MultiPackageProposal proposal) =>
        _joinableTaskFactory.RunAsync(() => ShowAsync(solutionDirectory, proposal)).FileAndForget("daml/languageClient/multiPackageOffer");

    private async Task ShowAsync(string solutionDirectory, MultiPackageProposal proposal)
    {
        var names = string.Join(", ", proposal.Packages.Select(Path.GetFileName));
        var (text, action) = proposal.Kind == MultiPackageAdvice.Create
            ? ($"The Daml packages of this solution ({names}) aren't listed together in a multi-package.yaml, so Go To Definition from one into another opens a read-only copy instead of its source. ", CreateAction)
            : ($"multi-package.yaml in the solution folder doesn't list these Daml packages of the solution: {names}. Go To Definition into them opens read-only copies. ", OpenAction);
        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan(text) },
            new[] { new InfoBarHyperlink(action), new InfoBarHyperlink(DeclineAction) },
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);
        var infoBar = await VS.InfoBar.CreateAsync(model);
        if (infoBar is null)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, text);
            return;
        }
        infoBar.ActionItemClicked += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
            var clicked = args.ActionItem.Text;
            args.InfoBarUIElement.Close();
            _joinableTaskFactory.RunAsync(() => ActAsync(clicked, solutionDirectory, proposal)).FileAndForget("daml/languageClient/multiPackageOfferAction");
        };
        if (!await infoBar.TryShowInfoBarUIAsync())
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, text);
        }
    }

    private async Task ActAsync(string action, string solutionDirectory, MultiPackageProposal proposal)
    {
        await TaskScheduler.Default;
        try
        {
            switch (action)
            {
                case CreateAction:
                    if (_offer.Create(solutionDirectory, proposal))
                    {
                        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"Created {proposal.ManifestPath}; the language server restarts to use it.");
                        // The same restart a saved multi-package.yaml asks for, so a watcher event can't add a second one.
                        _host.OnFileChanged(proposal.ManifestPath);
                    }
                    else
                    {
                        _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Warning, $"{proposal.ManifestPath} appeared in the meantime and was left as it is.");
                    }
                    await VS.Documents.OpenAsync(proposal.ManifestPath);
                    break;
                case OpenAction:
                    await VS.Documents.OpenAsync(proposal.ManifestPath);
                    break;
                case DeclineAction:
                    _offer.Dismiss(solutionDirectory);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Error, $"Couldn't write in {solutionDirectory}: {ex.Message}", ex);
        }
    }
}
