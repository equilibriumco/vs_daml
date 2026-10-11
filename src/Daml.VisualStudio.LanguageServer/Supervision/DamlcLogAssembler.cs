using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Daml.VisualStudio.Core.Logging;

namespace Daml.VisualStudio.LanguageServer.Supervision;

/// <summary>One damlc log entry, ready for the Output window.</summary>
/// <param name="Level">damlc's own level for it.</param>
/// <param name="Text">The component and message, without damlc's timestamp.</param>
public readonly record struct DamlcLogRecord(DamlLogLevel Level, string Text);

/// <summary>
/// Puts damlc's stderr back together into log entries. damlc writes each entry as a blank line,
/// a header (<c>2026-10-11 19:55:47.24 [INFO]  [Multi-IDE] </c>) and the message on the lines
/// after it, so an entry only ends when the next one starts; <see cref="Flush"/> ends it early
/// when the output goes quiet. Lines outside any entry pass straight through.
/// </summary>
public sealed class DamlcLogAssembler
{
    private static readonly Regex Header = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? \[(?<level>[A-Z]+)\]\s*(?<rest>.*)$",
        RegexOptions.CultureInvariant);

    private DamlLogLevel _level;
    private StringBuilder? _text;
    private bool _hasMessage;

    /// <summary>Whether an entry has started and not been emitted yet.</summary>
    public bool HasPending => _text is not null;

    /// <summary>Takes one stderr line and returns the entries it completes.</summary>
    public IReadOnlyList<DamlcLogRecord> Add(string line)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }

        var completed = new List<DamlcLogRecord>(1);
        if (Header.Match(line) is { Success: true } header)
        {
            if (Flush() is { } previous)
            {
                completed.Add(previous);
            }
            _level = LevelOf(header.Groups["level"].Value);
            var rest = header.Groups["rest"].Value.Trim();
            _text = new StringBuilder(rest);
            _hasMessage = false;
            return completed;
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            if (Flush() is { } previous)
            {
                completed.Add(previous);
            }
            return completed;
        }
        if (_text is null)
        {
            completed.Add(new DamlcLogRecord(DamlLogLevel.Information, line));
            return completed;
        }

        // The first message line continues the header; later ones go on their own lines.
        if (_hasMessage)
        {
            _text.Append('\n');
        }
        else if (_text.Length > 0)
        {
            _text.Append(' ');
        }
        _text.Append(line.TrimEnd());
        _hasMessage = true;
        return completed;
    }

    /// <summary>Emits the entry in progress, if any.</summary>
    public DamlcLogRecord? Flush()
    {
        if (_text is null)
        {
            return null;
        }
        var record = new DamlcLogRecord(_level, _text.ToString());
        _text = null;
        return record.Text.Length == 0 ? null : record;
    }

    private static DamlLogLevel LevelOf(string level) => level switch
    {
        "DEBUG" => DamlLogLevel.Debug,
        "WARNING" or "WARN" => DamlLogLevel.Warning,
        "ERROR" => DamlLogLevel.Error,
        _ => DamlLogLevel.Information,
    };
}
