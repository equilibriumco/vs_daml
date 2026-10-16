using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Daml.Build.Core.Tools;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

// Runs "dpm --version" and remembers the answer for the life of the MSBuild node, keyed by the
// file's path, size and time, so a build doesn't start a process per project. Updating dpm
// rewrites its dpm.cmd wrapper, which changes the key.
internal sealed class DpmVersionProbe : ToolTask
{
    private static readonly ConcurrentDictionary<string, Version> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dpm;
    private readonly List<string> _output = [];

    private DpmVersionProbe(string dpm) => _dpm = dpm;

    protected override string ToolName => "dpm";

    protected override Encoding StandardOutputEncoding => Utf8;

    protected override Encoding StandardErrorEncoding => Utf8;

    // The version of the dpm at that path, and what it printed when it had to be asked.
    public static (Version? Version, IReadOnlyList<string> Output) Read(string dpm, IBuildEngine engine, string[]? environment)
    {
        var file = new FileInfo(dpm);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        if (Known.TryGetValue(key, out var known))
        {
            return (known, []);
        }
        var probe = new DpmVersionProbe(dpm)
        {
            BuildEngine = engine,
            EnvironmentVariables = environment,
            Timeout = 60_000,
        };
        var version = probe.Execute() ? DpmVersion.Parse(probe._output) : null;
        if (version is not null)
        {
            Known[key] = version;
        }
        return (version, probe._output);
    }

    protected override string GenerateFullPathToTool() => _dpm;

    protected override string GenerateCommandLineCommands() => string.Join(" ", DpmVersion.Arguments);

    protected override void LogEventsFromTextOutput(string singleLine, MessageImportance messageImportance)
    {
        Log.LogMessage(MessageImportance.Low, singleLine);
        _output.Add(singleLine);
    }

    // The caller explains a dpm that can't say its version; MSBuild's exit-code error wouldn't.
    protected override bool HandleTaskExecutionErrors() => false;
}
