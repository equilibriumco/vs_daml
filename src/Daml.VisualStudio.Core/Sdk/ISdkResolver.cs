using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Processes;

namespace Daml.VisualStudio.Core.Sdk;

/// <summary>Asks one kind of Daml tool which SDK version it would use.</summary>
public interface ISdkResolver
{
    /// <summary>The tool this resolver understands.</summary>
    AssistantKind Kind { get; }

    /// <summary>
    /// Runs the tool's version command in <paramref name="workingDirectory"/> and returns the SDK
    /// version it reports, or <c>null</c> if it reports none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tool exited with an error.</exception>
    /// <exception cref="FormatException">The tool printed something this resolver doesn't understand.</exception>
    /// <exception cref="TimeoutException">The tool didn't answer in time.</exception>
    Task<string?> ReadSdkVersionAsync(AssistantLocation assistant, string workingDirectory, CancellationToken cancellationToken);
}

internal static class VersionCommand
{
    // Listing versions is local and quick; a minute only covers a cold start on a slow disk.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<string> RunAsync(IProcessRunner runner, AssistantLocation assistant, string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var spec = new ProcessSpec(assistant.Path, arguments) { WorkingDirectory = workingDirectory, Timeout = Timeout };
        var result = await runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var output = (result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout).Trim();
            throw new InvalidOperationException($"'{AssistantLocator.ToolName(assistant.Kind)} {string.Join(" ", arguments)}' failed with exit code {result.ExitCode}: {output}");
        }
        return result.Stdout;
    }
}
