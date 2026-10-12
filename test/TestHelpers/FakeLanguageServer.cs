using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Daml.TestHelpers;

/// <summary>
/// The fake Daml language server plus a temp folder holding its scenario and the log of
/// methods it received. <see cref="Start"/> launches one process per session.
/// </summary>
public sealed class FakeLanguageServer : IDisposable
{
    private readonly string _root;
    private readonly string _scenarioPath;
    private readonly string _callsPath;

    private FakeLanguageServer(string root)
    {
        _root = root;
        _scenarioPath = Path.Combine(root, "scenario.json");
        _callsPath = Path.Combine(root, "calls.jsonl");
        ExecutablePath = BuiltFakes.Executable("FakeDamlLanguageServerDirectory", "FakeDamlLanguageServer.exe");
    }

    /// <summary>Full path of the fake server executable.</summary>
    public string ExecutablePath { get; }

    /// <summary>Variables the fake needs to find its scenario and call log.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["FAKE_LS_SCENARIO"] = _scenarioPath,
        ["FAKE_LS_CALLS"] = _callsPath,
    };

    /// <summary>Prepares a fake server with <paramref name="scenario"/>.</summary>
    public static FakeLanguageServer Create(FakeLanguageServerScenario scenario)
    {
        var server = new FakeLanguageServer(Path.Combine(Path.GetTempPath(), "daml-fake-ls-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(server._root);
        var json = new JObject
        {
            ["capabilities"] = scenario.Capabilities,
            ["stderrLines"] = new JArray(scenario.StderrLines),
            ["exitCodeAtStart"] = scenario.ExitCodeAtStart is { } code ? new JValue(code) : JValue.CreateNull(),
            ["hangOnInitialize"] = scenario.HangOnInitialize,
            ["hangOnKeepAlive"] = scenario.HangOnKeepAlive,
            ["notificationsAfterInitialized"] = new JArray(scenario.NotificationsAfterInitialized),
        };
        File.WriteAllText(server._scenarioPath, json.ToString(Formatting.None));
        return server;
    }

    /// <summary>Starts a server process and connects a JSON-RPC client to it.</summary>
    public FakeLanguageServerSession Start() => FakeLanguageServerSession.Start(ExecutablePath, Environment);

    /// <summary>Methods received across all sessions, in order.</summary>
    public IReadOnlyList<string> ReadReceivedMethods()
    {
        if (!File.Exists(_callsPath))
        {
            return Array.Empty<string>();
        }
        // The server may be appending right now: share the file and skip a line it hasn't finished.
        using var stream = new FileStream(_callsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var methods = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            try
            {
                methods.Add((string)JObject.Parse(line)["method"]!);
            }
            catch (JsonReaderException)
            {
            }
        }
        return methods;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
