namespace Daml.TestHelpers;

/// <summary>Outcome of a finished process.</summary>
public sealed class ProcessResult
{
    /// <summary>Creates a result.</summary>
    public ProcessResult(int exitCode, string stdout, string stderr)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
    }

    /// <summary>Exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Everything written to stdout.</summary>
    public string Stdout { get; }

    /// <summary>Everything written to stderr.</summary>
    public string Stderr { get; }
}
