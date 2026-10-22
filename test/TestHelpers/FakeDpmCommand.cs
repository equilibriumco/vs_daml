using System.Collections.Generic;

namespace Equilibrium.Daml.TestHelpers;

/// <summary>One scripted dpm invocation and its outcome.</summary>
public sealed class FakeDpmCommand
{
    /// <summary>Exact argument list to match; "*" matches any single argument.</summary>
    public List<string> Args { get; } = new();

    /// <summary>Written to stdout as-is.</summary>
    public string Stdout { get; set; } = "";

    /// <summary>Written to stderr as-is.</summary>
    public string Stderr { get; set; } = "";

    /// <summary>Process exit code.</summary>
    public int ExitCode { get; set; }

    /// <summary>How long to wait, after writing the output, before exiting; for timeouts and cancellation.</summary>
    public int DelayMilliseconds { get; set; }

    /// <summary>
    /// Files to create, keyed by path relative to the working directory, or, starting with
    /// <c>{out}/</c>, relative to the folder given after <c>--out</c>. Content starting with
    /// <c>base64:</c> is written as the bytes it encodes.
    /// </summary>
    public Dictionary<string, string> WriteFiles { get; } = new();
}
