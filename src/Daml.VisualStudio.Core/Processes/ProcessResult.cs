namespace Daml.VisualStudio.Core.Processes;

/// <summary>Outcome of a process that ran to completion.</summary>
/// <param name="ExitCode">Exit code.</param>
/// <param name="Stdout">Everything written to standard output, decoded as UTF-8.</param>
/// <param name="Stderr">Everything written to standard error, decoded as UTF-8.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
