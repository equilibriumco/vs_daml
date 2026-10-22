using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Options;

namespace Daml.VisualStudio.Core.Diagnostics;

/// <summary>What goes into a diagnostic report besides the log files.</summary>
public sealed record DiagnosticReportInput
{
    /// <summary>The extension's version.</summary>
    public string ExtensionVersion { get; init; } = "unknown";

    /// <summary>Visual Studio's version.</summary>
    public string HostVersion { get; init; } = "unknown";

    /// <summary>The user's Daml settings.</summary>
    public DamlSettings Settings { get; init; } = new();

    /// <summary>The folder of the extension's log files.</summary>
    public string LogFolder { get; init; } = "";

    /// <summary>Most characters of log text included; the newest logs come first and the oldest are left out.</summary>
    public long MaxLogCharacters { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// A plain-text report for a bug report: versions, settings and the newest log files, all
/// passed through a <see cref="DiagnosticRedactor"/>. Plain text, so the user can read exactly
/// what they are about to share.
/// </summary>
public static class DiagnosticReport
{
    /// <summary><c>%LOCALAPPDATA%\DamlVisualStudio\diagnostics</c>, beside the log files.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamlVisualStudio", "diagnostics");

    /// <summary>The report's text.</summary>
    public static string Create(DiagnosticReportInput input, DiagnosticRedactor redactor, TimeProvider time)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        if (redactor is null)
        {
            throw new ArgumentNullException(nameof(redactor));
        }
        if (time is null)
        {
            throw new ArgumentNullException(nameof(time));
        }

        var report = new StringBuilder();
        report.Append("Daml for Visual Studio diagnostics\r\n");
        report.Append("Created: ").Append(time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append("\r\n");
        report.Append("Paths, user and machine names, source code, script results and secrets were masked where recognised.\r\n");
        report.Append("Read it before you share it, and remove anything you don't want to share.\r\n\r\n");

        var header = new StringBuilder();
        header.Append("[Environment]\r\n");
        header.Append("Extension: ").Append(input.ExtensionVersion).Append("\r\n");
        header.Append("Visual Studio: ").Append(input.HostVersion).Append("\r\n");
        header.Append("Windows: ").Append(Environment.OSVersion.VersionString).Append("\r\n");
        header.Append("Process: ").Append(Environment.Is64BitProcess ? "64-bit" : "32-bit").Append(", ").Append(RuntimeInformation.FrameworkDescription).Append("\r\n\r\n");
        AppendSettings(header, input.Settings);
        report.Append(redactor.Redact(header.ToString()));

        AppendLogs(report, input, redactor);
        return report.ToString();
    }

    /// <summary>Writes the report to a new <c>daml-diagnostics-&lt;local time&gt;.txt</c> in <paramref name="folder"/> and returns its path.</summary>
    /// <exception cref="IOException">The folder or file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static string Write(string folder, DiagnosticReportInput input, DiagnosticRedactor redactor, TimeProvider time)
    {
        if (folder is null)
        {
            throw new ArgumentNullException(nameof(folder));
        }

        var text = Create(input, redactor, time);
        Directory.CreateDirectory(folder);
        var stem = "daml-diagnostics-" + time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".txt");
        for (var n = 1; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.txt");
        }
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static void AppendSettings(StringBuilder report, DamlSettings settings)
    {
        report.Append("[Settings]\r\n");
        Setting(report, nameof(DamlSettings.DpmPath), settings.DpmPath);
        Setting(report, nameof(DamlSettings.DamlAssistantPath), settings.DamlAssistantPath);
        Setting(report, nameof(DamlSettings.UseMultiIde), settings.UseMultiIde);
        Setting(report, nameof(DamlSettings.LogLevel), settings.LogLevel);
        Setting(report, nameof(DamlSettings.ExtraArguments), settings.ExtraArguments);
        Setting(report, nameof(DamlSettings.AutorunAllTests), settings.AutorunAllTests);
        Setting(report, nameof(DamlSettings.KeepAliveEnabled), settings.KeepAliveEnabled);
        Setting(report, nameof(DamlSettings.LiveBindingSync), settings.LiveBindingSync);
        report.Append("\r\n");

        static void Setting(StringBuilder report, string name, object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            report.Append(name).Append(": ").Append(string.IsNullOrWhiteSpace(text) ? "(not set)" : text).Append("\r\n");
        }
    }

    private static void AppendLogs(StringBuilder report, DiagnosticReportInput input, DiagnosticRedactor redactor)
    {
        report.Append("[Logs]\r\n");
        var files = string.IsNullOrEmpty(input.LogFolder) ? [] : RollingLogFile.Files(input.LogFolder);
        if (files.Count == 0)
        {
            report.Append("No log files.\r\n");
            return;
        }

        var left = input.MaxLogCharacters;
        foreach (var file in files)
        {
            if (left <= 0)
            {
                report.Append("--- ").Append(file.Name).Append(" (left out: over the size limit) ---\r\n");
                continue;
            }

            string text;
            try
            {
                text = ReadShared(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Append("--- ").Append(file.Name).Append(" (couldn't be read) ---\r\n");
                continue;
            }

            // Section headers determine how continuation lines are masked. Keep that
            // context until redaction finishes, even when only the tail will be exported.
            text = redactor.Redact(text);
            report.Append("--- ").Append(file.Name).Append(" ---\r\n");
            if (text.Length > left)
            {
                // Keep the end, from the first whole line.
                var start = text.Length - (int)left;
                var lineStart = text.IndexOf('\n', start);
                text = lineStart < 0 ? "" : text.Substring(lineStart + 1);
                report.Append("(the beginning is left out)\r\n");
            }
            left -= text.Length;
            report.Append(text);
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal))
            {
                report.Append("\r\n");
            }
        }
    }

    // The writing process keeps appending; share both ways so neither side fails.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
