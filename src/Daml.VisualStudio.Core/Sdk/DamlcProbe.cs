using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Processes;

namespace Daml.VisualStudio.Core.Sdk;

/// <summary>What the damlc that would actually serve a folder can do.</summary>
/// <param name="Version">The version damlc prints (a component version, e.g. 3.6.2 for SDK 3.6.0), if it prints one.</param>
/// <param name="SupportsIde">Has the single-package <c>ide</c> server.</param>
/// <param name="SupportsMultiIde">Has the <c>multi-ide</c> server.</param>
/// <param name="SupportsIdeIdentifier"><c>multi-ide</c> accepts <c>--ide-identifier</c>.</param>
public sealed record DamlcCapabilities(string? Version, bool SupportsIde, bool SupportsMultiIde, bool SupportsIdeIdentifier);

/// <summary>
/// Asks damlc itself, through the chosen tool and in the server's working folder, so the answer
/// reflects the SDK the tool really picks there rather than a version number's promise.
/// </summary>
public sealed class DamlcProbe
{
    private static readonly string[] TopHelp = ["damlc", "--help"];
    private static readonly string[] MultiIdeHelp = ["damlc", "multi-ide", "--help"];
    private static readonly Regex VersionLine = new(@"^SDK Version:\s*(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _runner;

    /// <summary>Creates a probe running the tool through <paramref name="runner"/>.</summary>
    public DamlcProbe(IProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>Runs <c>damlc --help</c> (and <c>damlc multi-ide --help</c> when there is such a command).</summary>
    /// <exception cref="InvalidOperationException">damlc exited with an error.</exception>
    /// <exception cref="FormatException">The output doesn't look like damlc's help.</exception>
    /// <exception cref="TimeoutException">damlc didn't answer in time.</exception>
    public async Task<DamlcCapabilities> ProbeAsync(AssistantLocation assistant, string workingDirectory, CancellationToken cancellationToken)
    {
        var help = await VersionCommand.RunAsync(_runner, assistant, workingDirectory, TopHelp, cancellationToken).ConfigureAwait(false);
        var version = VersionLine.Match(help) is { Success: true } match ? match.Groups[1].Value : null;
        var hasIde = HasCommand(help, "ide");
        var hasMultiIde = HasCommand(help, "multi-ide");
        if (version is null && !hasIde && !hasMultiIde)
        {
            throw new FormatException($"'damlc --help' printed neither a version nor the language server commands: {help.Trim()}");
        }

        var hasIdeIdentifier = false;
        if (hasMultiIde)
        {
            var multiIde = await VersionCommand.RunAsync(_runner, assistant, workingDirectory, MultiIdeHelp, cancellationToken).ConfigureAwait(false);
            hasIdeIdentifier = multiIde.IndexOf("--ide-identifier", StringComparison.Ordinal) >= 0;
        }
        return new DamlcCapabilities(version, hasIde, hasMultiIde, hasIdeIdentifier);
    }

    // Commands are listed one per line, indented, name first, then the description.
    private static bool HasCommand(string help, string command) =>
        Regex.IsMatch(help, @"^\s+" + Regex.Escape(command) + @"\s", RegexOptions.Multiline | RegexOptions.CultureInvariant);
}
