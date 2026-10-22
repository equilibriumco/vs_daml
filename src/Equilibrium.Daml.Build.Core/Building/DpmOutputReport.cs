using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Building;

/// <summary>
/// Reads the output of one <c>dpm build</c> or <c>dpm test</c> of a package, line by line, into
/// what the build reports: damlc's diagnostics at their files and ranges, the DAR dpm created,
/// and why dpm failed when it says so (a missing SDK) or, failing that, the end of its output.
/// </summary>
public sealed class DpmOutputReport
{
    private const int TailLength = 10;

    // "daml/TypeError.daml:5:9: error:" opens damlc's text; the message's place says it already.
    private static readonly Regex RepeatedPlace = new(@"^\s*\S.*?:\d+:\d+:\s*(error|warning):\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly char[] Blanks = ['\r', '\n', ' ', '\t'];

    private readonly DpmCommandKind _kind;
    private readonly string _packageRoot;
    private readonly string _manifestPath;
    private readonly ToolOptions _options;
    private readonly DamlDiagnosticParser _parser = new();
    private readonly List<BuildMessage> _failures = [];
    private readonly Queue<string> _tail = new();
    private string? _created;

    /// <summary>Creates a report for one run.</summary>
    /// <param name="kind">What ran.</param>
    /// <param name="packageRoot">The folder dpm ran in; printed paths are relative to it.</param>
    /// <param name="manifestPath">The package's <c>daml.yaml</c>, where problems without a place of their own point.</param>
    /// <param name="options">How tools were allowed to run, for advice on a missing SDK.</param>
    public DpmOutputReport(DpmCommandKind kind, string packageRoot, string manifestPath, ToolOptions options)
    {
        _kind = kind;
        _packageRoot = packageRoot ?? throw new ArgumentNullException(nameof(packageRoot));
        _manifestPath = manifestPath ?? throw new ArgumentNullException(nameof(manifestPath));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Takes one line of standard output or standard error, as it arrives.</summary>
    public void AddLine(string line)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }
        _created = DarCreatedLine.TryParse(line) ?? _created;
        line = SecretRedaction.Redact(line);
        _parser.AddLine(line);

        var text = DamlDiagnosticParser.StripAnsi(line).Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (text.StartsWith("SDK_NOT_INSTALLED:", StringComparison.Ordinal))
        {
            _failures.Add(AtManifest(
                _options.Offline ? DamlDiagnosticCodes.MissingToolOffline : DamlDiagnosticCodes.MissingTool,
                text + (_options.Offline
                    ? " EqDamlOffline is true, so the build installs nothing; install it beforehand with that command."
                    : _options.AllowToolInstall ? "" : " Install it with that command, or set EqDamlAllowToolInstall to true to let the build install it.")));
        }
        else if (text.StartsWith("error while auto-installing", StringComparison.Ordinal))
        {
            _failures.Add(AtManifest(DamlDiagnosticCodes.MissingTool, text));
        }
        _tail.Enqueue(text);
        if (_tail.Count > TailLength)
        {
            _tail.Dequeue();
        }
    }

    /// <summary>Ends the run. A failure nothing explains gets the end of the output as its message.</summary>
    /// <param name="exitCode">dpm's exit code.</param>
    public DpmOutcome Complete(int exitCode)
    {
        _parser.Complete();
        var messages = _parser.Diagnostics.Select(FromDiagnostic).ToList();
        messages.AddRange(_failures);
        if (exitCode != 0 && !messages.Any(m => m.Severity == DamlDiagnosticSeverity.Error))
        {
            var verb = _kind.ToString().ToLowerInvariant();
            messages.Add(AtManifest(DamlDiagnosticCodes.CompileError, $"dpm {verb} failed with exit code {exitCode}:\n" + string.Join("\n", _tail)));
        }
        return new DpmOutcome(messages, _created is null ? null : DamlPath.Resolve(_packageRoot, _created));
    }

    private BuildMessage FromDiagnostic(DamlDiagnostic diagnostic)
    {
        var code = diagnostic.Severity switch
        {
            DamlDiagnosticSeverity.Error => DamlDiagnosticCodes.CompileError,
            DamlDiagnosticSeverity.Warning => DamlDiagnosticCodes.CompileWarning,
            _ => null,
        };
        var text = string.Join(" ", RepeatedPlace.Replace(diagnostic.Message, "").Split(Blanks, StringSplitOptions.RemoveEmptyEntries));
        var range = diagnostic.Range;
        return new BuildMessage(
            diagnostic.Severity,
            code,
            diagnostic.Source,
            diagnostic.ResolvePath(_packageRoot),
            range?.StartLine ?? 0,
            range?.StartColumn ?? 0,
            range?.EndLine ?? 0,
            range?.EndColumn ?? 0,
            text);
    }

    private BuildMessage AtManifest(string code, string text) =>
        new(DamlDiagnosticSeverity.Error, code, null, _manifestPath, 0, 0, 0, 0, text);
}
