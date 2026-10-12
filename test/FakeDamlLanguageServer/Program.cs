using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Daml.TestFakes.LanguageServer;

/// <summary>
/// Fake Daml language server speaking LSP (Content-Length framing) over stdin/stdout.
/// FAKE_LS_SCENARIO points at a JSON file:
/// <c>{ "capabilities": { ... }, "stderrLines": [...], "exitCodeAtStart": 1, "hangOnInitialize": true, "hangOnKeepAlive": true,</c>
/// <c>"notificationsAfterInitialized": [ { "method": "...", "params": { ... } } ] }</c>
/// (all but capabilities optional): lines written to stderr at start, an immediate exit, an
/// initialize or a daml/keepAlive that never gets a reply, and notifications sent after initialized.
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
        using var rpc = new JsonRpc(handler, server);
        server.Rpc = rpc;
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
}
