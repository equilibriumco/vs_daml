using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Daml.TestFakes.Dpm;

/// <summary>
/// Fake dpm. FAKE_DPM_SCENARIO points at a JSON file:
/// <code>
/// { "commands": [ { "args": ["codegen-cs", "--dar", "*"], "stdout": "...", "stderr": "...",
///                   "exitCode": 0, "writeFiles": { "out/A.cs": "..." } } ] }
/// </code>
/// The first entry whose args match wins; "*" matches any single argument. Files are written
/// relative to the working directory. Every invocation is appended to FAKE_DPM_CALLS as one
/// JSON line, before the command runs, so even a crash leaves a trace.
/// </summary>
internal static class Program
{
    // Kept in sync with FakeDpm.UnscriptedExitCode / ScenarioErrorExitCode in TestHelpers.
    private const int UnscriptedExitCode = 97;
    private const int ScenarioErrorExitCode = 98;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static int Main(string[] args)
    {
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

        foreach (var (relativePath, content) in command.WriteFiles)
        {
            var target = Path.GetFullPath(relativePath, Environment.CurrentDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        Console.Out.Write(command.Stdout);
        Console.Error.Write(command.Stderr);
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
                     || e.Key.Equals("JAVA_HOME", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key.ToUpperInvariant(), e => e.Value);

        var line = JsonSerializer.Serialize(new { args, workingDirectory = Environment.CurrentDirectory, environment }, Json);
        File.AppendAllText(callsPath, line + Environment.NewLine);
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
        public Dictionary<string, string> WriteFiles { get; set; } = new();
    }
}
