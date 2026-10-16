using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Daml.VisualStudio.Core.Processes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Daml.TestHelpers;

/// <summary>
/// A throwaway copy of the fake dpm in its own temp directory (<c>bin\dpm.exe</c>), wired to a
/// scenario and a call log. Put <see cref="BinDirectory"/> on PATH or pass
/// <see cref="ExecutablePath"/> to the code under test; set <see cref="Environment"/> on the
/// child process. Dispose deletes everything.
/// </summary>
public sealed class FakeDpm : IDisposable
{
    /// <summary>Exit code for a command the scenario doesn't cover.</summary>
    public const int UnscriptedExitCode = FakeDpmExitCodes.Unscripted;

    /// <summary>Exit code when the scenario file is missing or invalid.</summary>
    public const int ScenarioErrorExitCode = FakeDpmExitCodes.ScenarioError;

    private static readonly JsonSerializerSettings ScenarioJson = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    private static readonly char[] CharsNeedingQuotes = { ' ', '\t', '\n', '\v', '"' };

    private readonly string _scenarioPath;
    private readonly string _callsPath;

    private FakeDpm(string root)
    {
        RootDirectory = root;
        BinDirectory = Path.Combine(root, "bin");
        ExecutablePath = Path.Combine(BinDirectory, "dpm.exe");
        _scenarioPath = Path.Combine(root, "scenario.json");
        _callsPath = Path.Combine(root, "calls.jsonl");
    }

    /// <summary>Temp directory owned by this instance; tests may create working folders under it.</summary>
    public string RootDirectory { get; }

    /// <summary>Directory containing <c>dpm.exe</c>.</summary>
    public string BinDirectory { get; }

    /// <summary>Full path of the fake <c>dpm.exe</c>.</summary>
    public string ExecutablePath { get; }

    /// <summary>Variables the fake needs to find its scenario and call log.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["FAKE_DPM_SCENARIO"] = _scenarioPath,
        ["FAKE_DPM_CALLS"] = _callsPath,
    };

    /// <summary>Copies the built fake into a fresh temp directory and writes <paramref name="scenario"/>.</summary>
    public static FakeDpm Create(FakeDpmScenario scenario)
    {
        var source = Path.GetDirectoryName(BuiltFakes.Executable("FakeDpmDirectory", "dpm.exe"))!;
        var fake = new FakeDpm(Path.Combine(Path.GetTempPath(), "daml-fake-dpm-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fake.BinDirectory);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(fake.BinDirectory, Path.GetFileName(file)));
        }

        fake.SetScenario(scenario);
        return fake;
    }

    /// <summary>Replaces the scenario; takes effect on the next invocation.</summary>
    public void SetScenario(FakeDpmScenario scenario) =>
        File.WriteAllText(_scenarioPath, JsonConvert.SerializeObject(scenario, ScenarioJson));

    /// <summary>Runs the fake directly, mainly for testing the fake itself.</summary>
    public Task<ProcessResult> RunAsync(string workingDirectory, params string[] args) =>
        RunAsync(workingDirectory, new Dictionary<string, string>(), args);

    /// <summary>Runs the fake with extra environment variables.</summary>
    public async Task<ProcessResult> RunAsync(string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            // ArgumentList doesn't exist on net48, so quote by hand.
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var pair in Environment.Concat(environment))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the fake dpm.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>Every invocation so far, oldest first.</summary>
    public IReadOnlyList<FakeDpmCall> ReadCalls()
    {
        if (!File.Exists(_callsPath))
        {
            return Array.Empty<FakeDpmCall>();
        }

        return File.ReadAllLines(_callsPath)
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                var json = JObject.Parse(line);
                return new FakeDpmCall(
                    json["args"]!.ToObject<string[]>()!,
                    (string)json["workingDirectory"]!,
                    json["environment"]!.ToObject<Dictionary<string, string>>()!,
                    (int?)json["processId"] ?? 0);
            })
            .ToList();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A process the test started may still hold a file; the temp folder gets cleaned eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Inverse of CommandLineToArgvW: backslashes are literal unless they precede a quote, so a
    // run of them before a quote (or before the closing quote) has to be doubled.
    private static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(CharsNeedingQuotes) < 0)
        {
            return arg;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            quoted.Append(c);
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
