using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Processes;

namespace Daml.VisualStudio.Core.Sdk;

/// <summary>
/// Reads the SDK the daml assistant would use from <c>daml version</c>, run in the project
/// folder so the project's daml.yaml is taken into account.
/// </summary>
public sealed class DamlAssistantSdkResolver : ISdkResolver
{
    private static readonly string[] Arguments = ["version"];

    // Annotations on the version list, strongest first: an explicit DAML_SDK_VERSION, then the
    // project's daml.yaml, then the global default.
    private static readonly string[] Priority =
    [
        "selected by env var",
        "project SDK version from daml.yaml",
        "default SDK version for new projects",
    ];

    private readonly IProcessRunner _runner;

    /// <summary>Creates a resolver running the daml assistant through <paramref name="runner"/>.</summary>
    public DamlAssistantSdkResolver(IProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <inheritdoc />
    public AssistantKind Kind => AssistantKind.DamlAssistant;

    /// <inheritdoc />
    public async Task<string?> ReadSdkVersionAsync(AssistantLocation assistant, string workingDirectory, CancellationToken cancellationToken)
    {
        var output = await VersionCommand.RunAsync(_runner, assistant, workingDirectory, Arguments, cancellationToken).ConfigureAwait(false);
        return ParseSelectedVersion(output);
    }

    /// <summary>The version on the highest-priority annotated line, or <c>null</c> if no line is annotated.</summary>
    public static string? ParseSelectedVersion(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var label in Priority)
        {
            foreach (var line in lines)
            {
                if (line.IndexOf(label, StringComparison.Ordinal) >= 0)
                {
                    return line.Trim().Split([' ', '\t'], 2)[0];
                }
            }
        }
        return null;
    }
}
