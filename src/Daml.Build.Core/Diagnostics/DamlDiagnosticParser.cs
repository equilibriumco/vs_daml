using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Daml.Build.Core.Diagnostics;

/// <summary>
/// Reads the diagnostic blocks dpm prints while building or testing, line by line, so it can
/// follow output as it streams. A block looks like this:
/// <code>
/// File:     daml/TypeError.daml
/// Hidden:   no
/// Range:    5:9-5:20
/// Source:   typecheck
/// Severity: DsError
/// Message:
/// daml/TypeError.daml:5:9: error:
///     • Couldn't match expected type ‘Int’ with actual type ‘Text’
/// </code>
/// Nothing marks the end of a message, so it ends at the next block, a blank line, a
/// timestamped log line, dpm's <c>ERROR:</c> summary or a <c>damlc:</c> failure line.
/// Everything outside blocks is ignored, and so are the ANSI colour codes damlc puts in messages.
/// </summary>
public sealed class DamlDiagnosticParser
{
    private static readonly Regex Field = new(@"^(File|Hidden|Range|Source|Severity|Message):\s?(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex RangeValue = new(@"^(\d+):(\d+)-(\d+):(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Building = new(@"^Building (\S.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex LogHeader = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d+)? \[", RegexOptions.CultureInvariant);
    // damlc colours its messages with ANSI escapes even when its output isn't a terminal.
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant);
    private static readonly Regex ToolFailure = new(@"^(ERROR: |(damlc|dpm|daml)(\.exe)?: )", RegexOptions.CultureInvariant);

    private readonly List<DamlDiagnostic> _diagnostics = new();
    private readonly List<string> _message = new();
    private string? _packageRoot;
    private Block? _block;
    private bool _inMessage;

    /// <summary>Diagnostics completed so far, in output order.</summary>
    public IReadOnlyList<DamlDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// <paramref name="line"/> without the ANSI colour codes damlc writes even when its output is
    /// redirected, for showing the line as plain text.
    /// </summary>
    public static string StripAnsi(string line) =>
        AnsiEscape.Replace(line ?? throw new ArgumentNullException(nameof(line)), "");

    /// <summary>Parses a whole output at once.</summary>
    public static IReadOnlyList<DamlDiagnostic> Parse(string output)
    {
        var parser = new DamlDiagnosticParser();
        foreach (var line in (output ?? throw new ArgumentNullException(nameof(output))).Split('\n'))
        {
            parser.AddLine(line);
        }
        parser.Complete();
        return parser.Diagnostics;
    }

    /// <summary>Feeds one line of output, with or without its line break.</summary>
    public void AddLine(string line)
    {
        line = AnsiEscape.Replace((line ?? throw new ArgumentNullException(nameof(line))).TrimEnd('\r', '\n'), "");

        var field = Field.Match(line);
        if (field.Success && field.Groups[1].Value == "File")
        {
            Finish();
            _block = new Block(field.Groups[2].Value.Trim());
            return;
        }

        if (_block is not null && !_inMessage)
        {
            if (field.Success)
            {
                SetField(_block, field.Groups[1].Value, field.Groups[2].Value.Trim());
                return;
            }
            if (line.Length == 0 || !EndsMessage(line))
            {
                return; // some field this parser doesn't know about
            }
        }
        else if (_block is not null)
        {
            if (!EndsMessage(line))
            {
                _message.Add(line.TrimEnd());
                return;
            }
        }

        Finish();
        if (Building.Match(line) is { Success: true } building)
        {
            _packageRoot = building.Groups[1].Value.Trim().Replace('/', '\\');
        }
    }

    /// <summary>Closes a block the output ended in the middle of.</summary>
    public void Complete() => Finish();

    private static bool EndsMessage(string line) =>
        line.Length == 0 || LogHeader.IsMatch(line) || ToolFailure.IsMatch(line);

    private void SetField(Block block, string name, string value)
    {
        switch (name)
        {
            case "Hidden":
                block.Hidden = value == "yes";
                break;
            case "Range":
                block.Range = ParseRange(value);
                break;
            case "Source":
                block.Source = value.Length > 0 ? value : null;
                break;
            case "Severity":
                block.Severity = value switch
                {
                    "DsWarning" => DamlDiagnosticSeverity.Warning,
                    "DsInfo" => DamlDiagnosticSeverity.Information,
                    "DsHint" => DamlDiagnosticSeverity.Hint,
                    _ => DamlDiagnosticSeverity.Error,
                };
                break;
            case "Message":
                _inMessage = true;
                if (value.Length > 0)
                {
                    _message.Add(value);
                }
                break;
        }
    }

    private static DamlRange? ParseRange(string value)
    {
        var match = RangeValue.Match(value);
        if (!match.Success)
        {
            return null;
        }
        int Part(int group) => int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return new DamlRange(Part(1), Part(2), Part(3), Part(4));
    }

    private void Finish()
    {
        if (_block is not null)
        {
            _diagnostics.Add(new DamlDiagnostic
            {
                File = _block.File,
                PackageRoot = _packageRoot,
                Range = _block.Range,
                Severity = _block.Severity,
                Source = _block.Source,
                Hidden = _block.Hidden,
                Message = string.Join("\n", _message).TrimEnd(),
            });
        }
        _block = null;
        _inMessage = false;
        _message.Clear();
    }

    private sealed class Block
    {
        public Block(string file) => File = file;

        public string File { get; }

        public bool Hidden { get; set; }

        public DamlRange? Range { get; set; }

        public string? Source { get; set; }

        public DamlDiagnosticSeverity Severity { get; set; } = DamlDiagnosticSeverity.Error;
    }
}
