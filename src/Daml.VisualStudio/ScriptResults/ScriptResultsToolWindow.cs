using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Daml.VisualStudio.ScriptResults;

/// <summary>
/// One window of script results. There is one per results resource, told apart by instance id;
/// <see cref="ScriptResultsWindows"/> creates them, fills them and shows them.
/// </summary>
[Guid(PackageGuids.ScriptResultsWindowString)]
public sealed class ScriptResultsToolWindow : ToolWindowPane
{
    /// <summary>Created by Visual Studio through the package.</summary>
    public ScriptResultsToolWindow()
        : base(null)
    {
        Caption = "Daml Script Results";
    }

    internal ScriptResultsView? View { get; private set; }

    internal void Attach(ScriptResultsView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        View = view;
        Content = view.Element;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Visual Studio disposes panes on the UI thread, where the browser control has to go;
            // the view checks that itself. A finalizer never gets here (disposing is false).
#pragma warning disable VSTHRD010
            View?.Dispose();
#pragma warning restore VSTHRD010
            View = null;
        }
        base.Dispose(disposing);
    }
}
