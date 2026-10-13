using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace Daml.TestHelpers;

/// <summary>A running fake server process and the LSP client connected to its stdio.</summary>
public sealed class FakeLanguageServerSession : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private FakeLanguageServerSession(Process process, JsonRpc rpc)
    {
        _process = process;
        Rpc = rpc;
    }

    /// <summary>Client side of the connection; already listening.</summary>
    public JsonRpc Rpc { get; }

    /// <summary>Whatever the server wrote to stderr so far.</summary>
    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    internal static FakeLanguageServerSession Start(string executable, IReadOnlyDictionary<string, string> environment, object? clientTarget)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the fake language server.");

        // Raw streams: LSP framing is bytes with Content-Length headers, not text lines.
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new JsonMessageFormatter()));
        if (clientTarget is not null)
        {
            rpc.AddLocalRpcTarget(clientTarget);
        }
        var session = new FakeLanguageServerSession(process, rpc);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (session._stderr)
                {
                    session._stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();
        rpc.StartListening();
        return session;
    }

    /// <summary>Closes the server's stdin, the way a client that goes away abruptly would.</summary>
    public void CloseInput() => _process.StandardInput.Close();

    /// <summary>Waits for the process to end and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        // Process.WaitForExitAsync doesn't exist on net48.
        var exited = await Task.Run(() => _process.WaitForExit((int)timeout.TotalMilliseconds)).ConfigureAwait(false);
        if (!exited)
        {
            throw new TimeoutException($"The fake language server didn't exit within {timeout}. Stderr:\n{Stderr}");
        }
        return _process.ExitCode;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Rpc.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        _process.Dispose();
    }
}
