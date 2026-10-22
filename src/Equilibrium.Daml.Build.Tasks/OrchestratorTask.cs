using System;
using System.IO;
using System.Threading;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Threading = System.Threading.Tasks;

namespace Equilibrium.Daml.Build.Tasks;

/// <summary>
/// A task whose work is the codegen orchestrator's: it waits for that work and passes MSBuild's
/// cancellation on to it.
/// </summary>
public abstract class OrchestratorTask : Task, ICancelableTask
{
    private readonly ICodegenOrchestrator? _orchestrator;
    private CancellationTokenSource? _running;
    private volatile bool _cancelled;

    /// <summary>Creates the task.</summary>
    /// <param name="orchestrator">The orchestrator to use; null for the one of this machine, made when the task runs.</param>
    private protected OrchestratorTask(ICodegenOrchestrator? orchestrator) => _orchestrator = orchestrator;

    /// <inheritdoc />
    public void Cancel()
    {
        _cancelled = true;
        try
        {
            Volatile.Read(ref _running)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between reading it and cancelling it.
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> and logs its messages; null, with EQD0110 logged, when the
    /// build was cancelled.
    /// </summary>
    private protected CodegenResult? Run(string projectPath, string whenCancelled, Func<ICodegenOrchestrator, CancellationToken, Threading.Task<CodegenResult>> work)
    {
        var orchestrator = _orchestrator ?? CodegenOrchestrator.ForThisMachine(Environment.GetEnvironmentVariable);
        CodegenResult result;
        using (var cancellation = new CancellationTokenSource())
        {
            Volatile.Write(ref _running, cancellation);
            if (_cancelled)
            {
                cancellation.Cancel();
            }
            try
            {
                // MSBuild calls Execute on a worker thread of its own, with no synchronization
                // context, and the core never needs this thread back, so waiting here can't
                // deadlock; Cancel reaches the work through the token.
#pragma warning disable VSTHRD002
                result = work(orchestrator, cancellation.Token).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }
            catch (OperationCanceledException)
            {
                Log.LogError(null, DamlDiagnosticCodes.CancelledOrTimedOut, null, projectPath, 0, 0, 0, 0, whenCancelled);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Most likely a file another process held for a moment, a DAR being written among them.
                Log.LogError(null, DamlDiagnosticCodes.CodegenFailed, null, projectPath, 0, 0, 0, 0,
                    $"The C# generation couldn't read or write a file: {ex.Message} Build again; if it keeps happening, look for what holds the file.");
                return null;
            }
            finally
            {
                Volatile.Write(ref _running, null);
            }
        }
        foreach (var message in result.Messages)
        {
            Log.Report(message);
        }
        return result;
    }
}
