using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.ScriptResults;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Equilibrium.Daml.VisualStudio.ScriptResults;

/// <summary>
/// Opens a Daml source at the lines a results link points to. The link has already been
/// checked; here the lines are checked against the document as it is now, since the results
/// may be older than the last edit.
/// </summary>
internal static class SourceReveal
{
    public static async Task RevealAsync(RevealRequest request, IDamlLogger logger, JoinableTaskFactory joinableTaskFactory)
    {
        await joinableTaskFactory.SwitchToMainThreadAsync();
        if (!File.Exists(request.File))
        {
            logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results link: {request.File} doesn't exist any more.");
            return;
        }
        IVsTextView view;
        try
        {
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, request.File, Guid.Empty, out _, out _, out _, out view);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            logger.Log(DamlLogCategory.Extension, DamlLogLevel.Warning, $"Script results link: couldn't open {request.File}.", ex);
            return;
        }
        if (view is null || !ErrorHandler.Succeeded(view.GetBuffer(out var buffer)) || !ErrorHandler.Succeeded(buffer.GetLineCount(out var lineCount)))
        {
            return;
        }
        if (!request.FitsIn(lineCount))
        {
            logger.Log(DamlLogCategory.Extension, DamlLogLevel.Information,
                $"Script results link: {Path.GetFileName(request.File)} has {lineCount} lines now, not lines {request.StartLine + 1}–{request.EndLine}; run the script again for current results.");
            return;
        }

        var (endLine, endColumn) = request.SelectionEnd(lineCount, line =>
        {
            buffer.GetLengthOfLine(line, out var length);
            return length;
        });
        view.SetSelection(request.StartLine, 0, endLine, endColumn);
        view.CenterLines(request.StartLine, Math.Max(1, endLine - request.StartLine + 1));
    }
}
