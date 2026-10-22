using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Bindings;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.Bindings;

/// <summary>Brings up "EQ Daml: C# References" with the uses of one declaration.</summary>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class CSharpReferencesWindow
{
    private readonly JoinableTaskFactory _joinableTaskFactory;

    [ImportingConstructor]
    public CSharpReferencesWindow(JoinableTaskContext joinableTaskContext) => _joinableTaskFactory = joinableTaskContext.Factory;

    /// <summary>Shows <paramref name="references"/> as the uses of <paramref name="declaration"/>, the window in front.</summary>
    public async Task ShowAsync(string declaration, IReadOnlyList<CSharpReference> references, CancellationToken cancellationToken)
    {
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var shell = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShell)) as IVsShell7
            ?? throw new InvalidOperationException("The shell service is not available.");
        var guid = new Guid(PackageGuids.PackageString);
        var package = await shell.LoadPackageAsync(ref guid) as AsyncPackage
            ?? throw new InvalidOperationException("The Daml package didn't load.");
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var window = (CSharpReferencesToolWindow)await package.FindToolWindowAsync(typeof(CSharpReferencesToolWindow), 0, create: true, cancellationToken);
        await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        window.Show(declaration, references);
        ErrorHandler.ThrowOnFailure(((IVsWindowFrame)window.Frame).Show());
    }
}
