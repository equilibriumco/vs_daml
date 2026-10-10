using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Daml.TestFakes.LanguageServer;

/// <summary>
/// Fake Daml language server speaking LSP (Content-Length framing) over stdin/stdout.
/// FAKE_LS_SCENARIO points at a JSON file: <c>{ "capabilities": { ... } }</c>.
/// FAKE_LS_CALLS, when set, receives every incoming method name as one JSON line.
/// Exit codes follow the LSP spec: 0 after shutdown + exit, 1 for exit without shutdown or a
/// closed input stream.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var scenario = LoadScenario();
        var server = new Server(scenario, Environment.GetEnvironmentVariable("FAKE_LS_CALLS"));

        var handler = new HeaderDelimitedMessageHandler(Console.OpenStandardOutput(), Console.OpenStandardInput(), new JsonMessageFormatter());
        using var rpc = new JsonRpc(handler, server);
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
        private bool _shutdownRequested;

        public Server(JObject scenario, string? callsPath)
        {
            _scenario = scenario;
            _callsPath = callsPath;
        }

        public Task<int> Exited => _exited.Task;

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public JObject Initialize(JToken? parameters)
        {
            Record("initialize");
            return new JObject
            {
                ["capabilities"] = _scenario["capabilities"]?.DeepClone() ?? new JObject(),
                ["serverInfo"] = new JObject { ["name"] = "fake-daml-language-server" },
            };
        }

        [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
        public void Initialized(JToken? parameters) => Record("initialized");

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

        private void Record(string method)
        {
            if (_callsPath is not null)
            {
                File.AppendAllText(_callsPath, new JObject { ["method"] = method }.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
            }
        }
    }
}
