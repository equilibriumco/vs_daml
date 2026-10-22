using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Equilibrium.Daml.TestHelpers;

namespace Equilibrium.Daml.TestFakes.Dpm;

/// <summary>
/// Fake dpm. FAKE_DPM_SCENARIO points at a JSON file:
/// <code>
/// { "commands": [ { "args": ["codegen-cs", "--dar", "*"], "stdout": "...", "stderr": "...",
///                   "exitCode": 0, "delayMilliseconds": 0, "writeFiles": { "out/A.cs": "..." } } ] }
/// </code>
/// The first entry whose args match wins; "*" matches any single argument. Files are written
/// relative to the working directory, or, starting with "{out}/", to the folder given after
/// "--out", as the generator writes its code; content starting with "base64:" is written as the bytes
/// it encodes, as a DAR is. Every invocation is appended to FAKE_DPM_CALLS as one
/// JSON line, before the command runs, so even a crash leaves a trace.
/// </summary>
internal static class Program
{
    private const int UnscriptedExitCode = FakeDpmExitCodes.Unscripted;
    private const int ScenarioErrorExitCode = FakeDpmExitCodes.ScenarioError;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static int Main(string[] args)
    {
        // Redirected console streams default to the OEM code page, which can't spell most of
        // what a real dpm prints.
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        RecordCall(args);

        var scenarioPath = Environment.GetEnvironmentVariable("FAKE_DPM_SCENARIO");
        if (string.IsNullOrEmpty(scenarioPath) || !File.Exists(scenarioPath))
        {
            Console.Error.WriteLine($"fake dpm: FAKE_DPM_SCENARIO not set or missing ('{scenarioPath}')");
            return ScenarioErrorExitCode;
        }

        Scenario scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(scenarioPath), Json) ?? new Scenario();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"fake dpm: invalid scenario '{scenarioPath}': {ex.Message}");
            return ScenarioErrorExitCode;
        }

        var command = scenario.Commands.FirstOrDefault(c => Matches(c.Args, args));
        if (command is null)
        {
            Console.Error.WriteLine($"fake dpm: no scenario entry for: {string.Join(" ", args)}");
            return UnscriptedExitCode;
        }

        const string OutToken = "{out}/";
        const string Base64Prefix = "base64:";
        var outIndex = Array.IndexOf(args, "--out");
        foreach (var (relativePath, content) in command.WriteFiles)
        {
            var target = relativePath.StartsWith(OutToken, StringComparison.Ordinal) && outIndex >= 0 && outIndex + 1 < args.Length
                ? Path.GetFullPath(relativePath.Substring(OutToken.Length), args[outIndex + 1])
                : Path.GetFullPath(relativePath, Environment.CurrentDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (content.StartsWith(Base64Prefix, StringComparison.Ordinal))
            {
                File.WriteAllBytes(target, Convert.FromBase64String(content.Substring(Base64Prefix.Length)));
            }
            else
            {
                File.WriteAllText(target, content);
            }
        }

        Console.Out.Write(command.Stdout);
        Console.Error.Write(command.Stderr);
        Console.Out.Flush();
        Console.Error.Flush();
        if (command.DelayMilliseconds > 0)
        {
            System.Threading.Thread.Sleep(command.DelayMilliseconds);
        }
        return command.ExitCode;
    }

    private static bool Matches(List<string> pattern, string[] args) =>
        pattern.Count == args.Length && pattern.Zip(args).All(p => p.First == "*" || p.First == p.Second);

    private static void RecordCall(string[] args)
    {
        var callsPath = Environment.GetEnvironmentVariable("FAKE_DPM_CALLS");
        if (string.IsNullOrEmpty(callsPath))
        {
            return;
        }

        // Only what dpm itself reacts to; the full environment would be noise and could leak secrets into logs.
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Key: (string)e.Key, Value: (string?)e.Value ?? ""))
            .Where(e => e.Key.StartsWith("DPM_", StringComparison.OrdinalIgnoreCase)
                     || e.Key.StartsWith("DAML_", StringComparison.OrdinalIgnoreCase)
                     || e.Key.StartsWith("EQDAML_", StringComparison.OrdinalIgnoreCase)
                     || e.Key.Equals("JAVA_HOME", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key.ToUpperInvariant(), e => e.Value);

        var line = JsonSerializer.Serialize(new { args, workingDirectory = Environment.CurrentDirectory, environment, processId = Environment.ProcessId }, Json);
        Append(callsPath, line + Environment.NewLine);
    }

    // Parallel builds start several dpms at once, and only one of them can write to the log; the
    // others wait their turn. The test reading it meanwhile is let in and skips an unfinished line.
    private static void Append(string path, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) when (attempt < 500)
            {
                System.Threading.Thread.Sleep(10);
            }
        }
    }

    private sealed class Scenario
    {
        public List<Command> Commands { get; set; } = new();
    }

    private sealed class Command
    {
        public List<string> Args { get; set; } = new();
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public int ExitCode { get; set; }
        public int DelayMilliseconds { get; set; }
        public Dictionary<string, string> WriteFiles { get; set; } = new();
    }
}
