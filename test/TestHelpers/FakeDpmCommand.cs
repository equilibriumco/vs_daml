using System.Collections.Generic;

namespace Daml.TestHelpers;

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

    /// <summary>Files to create, keyed by path relative to the working directory.</summary>
    public Dictionary<string, string> WriteFiles { get; } = new();
}
