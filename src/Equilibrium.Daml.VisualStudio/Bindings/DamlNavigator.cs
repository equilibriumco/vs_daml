using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>
/// Takes the user from C# to the Daml that the name at the caret stands for: straight there when
/// one current source has it, through a choice when several do, and otherwise to an explanation
/// in an info bar, with a build offered when the sources only need one.
/// </summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class DamlNavigator
{
    private const string BuildAction = "Build Solution";

    private readonly DamlBindingService _service;
    private readonly IDamlLogger _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;

    [ImportingConstructor]
    public DamlNavigator(DamlBindingService service, IDamlLogger logger, JoinableTaskContext joinableTaskContext)
    {
        _service = service;
        _logger = logger;
        _joinableTaskFactory = joinableTaskContext.Factory;
    }

    /// <summary>Goes to what <paramref name="found"/> stands for, starting from any thread.</summary>
    public void Go(BindingsAtPosition found, BindingIndex index) =>
        _joinableTaskFactory.RunAsync(() => GoAsync(found, index, CancellationToken.None)).FileAndForget("daml/bindings/goToDefinition");

    /// <summary>Goes to what <paramref name="found"/> stands for.</summary>
    public async Task GoAsync(BindingsAtPosition found, BindingIndex index, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        var plan = DamlNavigationPlanner.Plan(found.Symbols.Select(s => (s, _service.Sources.CandidatesFor(index, s))).ToList());
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Debug, $"Go to Daml definition of {string.Join(", ", found.Symbols)}: {plan.Kind}.");
        switch (plan.Kind)
        {
            case DamlNavigationKind.Go:
                await OpenAsync(plan.Targets[0], cancellationToken);
                break;
            case DamlNavigationKind.Choose:
                await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (DamlSourceChooser.Choose(plan.Message, plan.Targets) is { } chosen)
                {
                    await OpenAsync(chosen, cancellationToken);
                }
                break;
            default:
                await ShowAsync(plan.Message, offerBuild: plan.Kind == DamlNavigationKind.Stale);
                break;
        }
    }

    private async Task OpenAsync(DamlNavigationTarget target, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;
        var lookup = _service.Locator.Find(target.Symbol, target.Source.ManifestPath);
        if (lookup.Definition is not { } definition)
        {
            await ShowAsync(lookup.Problem!, offerBuild: true);
            return;
        }
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, definition.FilePath, Guid.Empty, out _, out _, out _, out IVsTextView view);
            if (view is not null)
            {
                view.SetCaretPos(definition.Line, definition.Column);
                view.CenterLines(definition.Line, 1);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Couldn't open {definition.FilePath}.", ex);
        }
    }

    private async Task ShowAsync(string message, bool offerBuild)
    {
        _logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information, message);
        await _joinableTaskFactory.SwitchToMainThreadAsync();
        var model = new InfoBarModel(
            [new InfoBarTextSpan(message + " ")],
            offerBuild ? [new InfoBarHyperlink(BuildAction)] : Array.Empty<InfoBarHyperlink>(),
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);
        if (await VS.InfoBar.CreateAsync(model) is not { } infoBar)
        {
            return;
        }
        infoBar.ActionItemClicked += (_, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // VS raises it from the info bar's UI
            if (args.ActionItem.Text == BuildAction)
            {
                args.InfoBarUIElement.Close();
                _joinableTaskFactory.RunAsync(() => VS.Commands.ExecuteAsync("Build.BuildSolution")).FileAndForget("daml/bindings/build");
            }
        };
        await infoBar.TryShowInfoBarUIAsync();
    }
}
