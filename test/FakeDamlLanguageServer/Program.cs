using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Equilibrium.Daml.TestFakes.LanguageServer;

/// <summary>
/// Fake Daml language server speaking LSP (Content-Length framing) over stdin/stdout.
/// FAKE_LS_SCENARIO points at a JSON file:
/// <c>{ "capabilities": { ... }, "stderrLines": [...], "stderrLinesAfterInitialized": [...], "exitCodeAtStart": 1,</c>
/// <c>"hangOnInitialize": true, "hangOnKeepAlive": true, "notificationsAfterInitialized": [ { "method": "...", "params": { ... } } ],</c>
/// <c>"replay": "path.jsonl", "replayRoot": "C:/work/repo" }</c>
/// (all optional): lines written to stderr at start or once the client says initialized, an
/// immediate exit, an initialize or a daml/keepAlive that never gets a reply, notifications
/// sent after initialized, and a recorded transcript to answer from. With a transcript, every
/// client message is matched to the next recorded one with the same method and answered with
/// what the real server sent then; <c>${ROOT}</c> in the recording becomes the replay root.
/// FAKE_LS_CALLS, when set, receives every incoming method name as one JSON line.
/// Exit codes follow the LSP spec: 0 after shutdown + exit, 1 for exit without shutdown or a
/// closed input stream.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var scenario = LoadScenario();
        foreach (var line in scenario["stderrLines"] ?? new JArray())
        {
            await Console.Error.WriteLineAsync((string?)line).ConfigureAwait(false);
        }
        await Console.Error.FlushAsync().ConfigureAwait(false);
        if (scenario["exitCodeAtStart"] is JValue { Type: JTokenType.Integer } exitCode)
        {
            return (int)exitCode;
        }

        var server = new Server(scenario, Environment.GetEnvironmentVariable("FAKE_LS_CALLS"));
        var handler = new HeaderDelimitedMessageHandler(Console.OpenStandardOutput(), Console.OpenStandardInput(), new JsonMessageFormatter());
        using var rpc = new JsonRpc(handler);
        server.Rpc = rpc;
        var replay = Replay.Load(scenario);
        if (replay is null)
        {
            rpc.AddLocalRpcTarget(server);
        }
        else
        {
            server.Register(rpc, replay);
        }
        rpc.StartListening();

        var finished = await Task.WhenAny(server.Exited, rpc.Completion).ConfigureAwait(false);
        return finished == server.Exited ? await server.Exited.ConfigureAwait(false) : 1;
    }

    private static JObject LoadScenario()
    {
        var path = Environment.GetEnvironmentVariable("FAKE_LS_SCENARIO");
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? new JObject() : JObject.Parse(File.ReadAllText(path));
    }

    // StreamJsonRpc binds incoming methods to public members of the target object.
    private sealed class Server
    {
        private readonly JObject _scenario;
        private readonly string? _callsPath;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _recordGate = new();
        private bool _shutdownRequested;

        public Server(JObject scenario, string? callsPath)
        {
            _scenario = scenario;
            _callsPath = callsPath;
        }

        public Task<int> Exited => _exited.Task;

        public JsonRpc? Rpc { get; set; }

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public async Task<JObject> Initialize(JToken? parameters)
        {
            Record("initialize");
            if (_scenario["hangOnInitialize"] is JValue { Type: JTokenType.Boolean } hang && (bool)hang)
            {
                await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
            }
            return new JObject
            {
                ["capabilities"] = _scenario["capabilities"]?.DeepClone() ?? new JObject(),
                ["serverInfo"] = new JObject { ["name"] = "fake-daml-language-server" },
            };
        }

        [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
        public async Task Initialized(JToken? parameters)
        {
            Record("initialized");
            foreach (var line in _scenario["stderrLinesAfterInitialized"] ?? new JArray())
            {
                await Console.Error.WriteLineAsync((string?)line).ConfigureAwait(false);
            }
            await Console.Error.FlushAsync().ConfigureAwait(false);
            foreach (var notification in _scenario["notificationsAfterInitialized"] ?? new JArray())
            {
                await Rpc!.NotifyWithParameterObjectAsync((string)notification["method"]!, notification["params"]).ConfigureAwait(false);
            }
        }

        [JsonRpcMethod("daml/keepAlive")]
        public async Task<object?> KeepAlive()
        {
            Record("daml/keepAlive");
            if (_scenario["hangOnKeepAlive"] is JValue { Type: JTokenType.Boolean } hang && (bool)hang)
            {
                await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
            }
            return null;
        }

        [JsonRpcMethod("daml/sdkInstallCancel", UseSingleObjectParameterDeserialization = true)]
        public void SdkInstallCancel(JToken? parameters) => Record("daml/sdkInstallCancel:" + (string?)parameters?["sdkVersionIdentifier"]);

        [JsonRpcMethod("shutdown")]
        public object? Shutdown()
        {
            Record("shutdown");
            _shutdownRequested = true;
            return null;
        }

        [JsonRpcMethod("exit")]
        public void Exit()
        {
            Record("exit");
            _exited.TrySetResult(_shutdownRequested ? 0 : 1);
        }

        // In replay mode the transcript answers everything it has an entry for; what it lacks,
        // and the two that end the process, are still handled here.
        public void Register(JsonRpc rpc, Replay replay)
        {
            foreach (var method in replay.ClientMethods.Where(m => m is not ("shutdown" or "exit")))
            {
                var handler = new ReplayHandler(this, rpc, replay, method);
                rpc.AddLocalRpcMethod(method, new Func<JToken?, Task<JToken?>>(handler.HandleAsync));
            }
            rpc.AddLocalRpcMethod("shutdown", new Func<object?>(Shutdown));
            rpc.AddLocalRpcMethod("exit", new Action(Exit));
            if (!replay.ClientMethods.Contains("daml/keepAlive"))
            {
                rpc.AddLocalRpcMethod("daml/keepAlive", new Func<Task<object?>>(KeepAlive));
            }
        }

        // One per replayed method: the attribute makes StreamJsonRpc hand over the params object
        // whole instead of matching its members to parameter names.
        private sealed class ReplayHandler(Server server, JsonRpc rpc, Replay replay, string method)
        {
            [JsonRpcMethod("replayed", UseSingleObjectParameterDeserialization = true)]
            public Task<JToken?> HandleAsync(JToken? parameters)
            {
                server.Record(method);
                return replay.AnswerAsync(rpc, method);
            }
        }

        // The test may be reading the file at the same moment, so it stays shared; one write per
        // line keeps a reader from seeing half of one for long.
        private void Record(string method)
        {
            if (_callsPath is null)
            {
                return;
            }
            var line = System.Text.Encoding.UTF8.GetBytes(new JObject { ["method"] = method }.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
            lock (_recordGate)
            {
                using var calls = new FileStream(_callsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                calls.Write(line, 0, line.Length);
            }
        }
    }

    // A recorded conversation, replayed in order: each client message consumes the next
    // recorded one with the same method, and the server messages recorded after it, up to the
    // reply or the next client message, are sent again.
    private sealed class Replay
    {
        private readonly List<(bool FromClient, JObject Message)> _entries;
        private readonly object _gate = new();
        private int _cursor = -1;

        private Replay(List<(bool FromClient, JObject Message)> entries) => _entries = entries;

        public HashSet<string> ClientMethods => _entries.Where(e => e.FromClient).Select(e => (string?)e.Message["method"]).OfType<string>().ToHashSet(StringComparer.Ordinal);

        public static Replay? Load(JObject scenario)
        {
            if ((string?)scenario["replay"] is not { Length: > 0 } path)
            {
                return null;
            }
            var root = ((string?)scenario["replayRoot"] ?? "C:/replay").Replace('\\', '/').TrimEnd('/');
            var text = File.ReadAllText(path)
                .Replace("${ROOT}", root)
                .Replace("${TEMP}", Path.GetTempPath().Replace('\\', '/').TrimEnd('/'))
                .Replace("${HOME}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/'));
            var entries = new List<(bool, JObject)>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }
                var entry = JObject.Parse(line);
                entries.Add(((string?)entry["direction"] == "client", (JObject)entry["message"]!));
            }
            return new Replay(entries);
        }

        public async Task<JToken?> AnswerAsync(JsonRpc rpc, string method)
        {
            int index;
            JToken? id;
            lock (_gate)
            {
                index = _entries.FindIndex(_cursor + 1, e => e.FromClient && (string?)e.Message["method"] == method);
                if (index < 0)
                {
                    return null; // nothing recorded for it: an empty answer, as damlc gives for the unknown
                }
                _cursor = index;
                id = _entries[index].Message["id"];
            }

            JToken? result = null;
            var error = (JObject?)null;
            for (var i = index + 1; i < _entries.Count; i++)
            {
                var (fromClient, message) = _entries[i];
                if (fromClient)
                {
                    if (message["method"] is null)
                    {
                        continue; // the client answering one of the server's requests; not a new turn
                    }
                    break;
                }
                if (message["method"] is null)
                {
                    if (id is not null && JToken.DeepEquals(message["id"], id))
                    {
                        result = message["result"];
                        error = message["error"] as JObject;
                        break;
                    }
                    continue; // a reply to some other request, not ours to send
                }
                var name = (string)message["method"]!;
                if (message["id"] is null)
                {
                    await rpc.NotifyWithParameterObjectAsync(name, message["params"]).ConfigureAwait(false);
                }
                else
                {
                    // The real server asked the client something (registerCapability, progress
                    // tokens); the answer doesn't matter here.
                    _ = rpc.InvokeWithParameterObjectAsync<JToken?>(name, message["params"]).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
            if (error is not null)
            {
                throw new LocalRpcException((string?)error["message"] ?? "error") { ErrorCode = (int?)error["code"] ?? -32000 };
            }
            return result;
        }
    }
}
