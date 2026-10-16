using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.FileSystem;
using Daml.Build.Core.Manifests;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Project;
using Daml.VisualStudio.Core.Sdk;
using Daml.VisualStudio.Core.Server;
using Daml.VisualStudio.LanguageServer.Hosting;
using Daml.VisualStudio.LanguageServer.Messages;
using Daml.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

// The tasks awaited here belong to this test session; there is no UI thread to deadlock.
#pragma warning disable VSTHRD003

namespace Daml.VisualStudio.LanguageServer.Tests.Contract;

// A real damlc started the way the extension starts it: workspace discovery, SDK resolution and
// the command builder pick the command, the process runs in a job, and the protocol adapter
// sits between the server and a JSON-RPC client that talks like Visual Studio. Every message is
// recorded, redacted, to artifacts/test/lsp-transcripts; with DAML_RECORD_TRANSCRIPTS=1 the
// transcripts also replace the ones under test/fixtures/lsp-transcripts.
internal sealed class RealDamlcSession : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly IRunningProcess _process;
    private readonly LspTranscriptRecorder _transcript;
    private readonly string _scenario;
    private readonly string _sdkVersion;
    private readonly Task _stderr;
    private readonly Dictionary<string, List<JArray>> _diagnostics = new(StringComparer.OrdinalIgnoreCase);

    private RealDamlcSession(IRunningProcess process, JsonRpc rpc, SdkResolution sdk, WorkspaceContext workspace, LspTranscriptRecorder transcript, string scenario, string sdkVersion)
    {
        _sdkVersion = sdkVersion;
        _process = process;
        _transcript = transcript;
        _scenario = scenario;
        Rpc = rpc;
        Sdk = sdk;
        Workspace = workspace;
        _stderr = process.StandardError.CopyToAsync(Stream.Null);
    }

    public JsonRpc Rpc { get; }

    public SdkResolution Sdk { get; }

    public WorkspaceContext Workspace { get; }

    public JObject Capabilities { get; private set; } = new();

    /// <summary>The redacted transcript so far, one JSON message per line.</summary>
    public IReadOnlyList<string> Transcript => _transcript.Lines();

    public static async Task<RealDamlcSession> StartAsync(SampleCopy sample, string activeFile, ServerMode mode, [CallerMemberName] string scenario = "")
    {
        var root = sample.Root;
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var files = new PhysicalFileSystem();
        var runner = new ProcessRunner();
        var workspace = new WorkspaceDiscovery(files, new YamlManifestReader(files)).Discover(root, activeFile, 1, mode)
            ?? throw new InvalidOperationException($"No Daml workspace for {activeFile}.");
        var options = new LanguageServerOptions { DpmPath = dpm };
        var sdk = await new SdkResolver(runner, new AssistantLocator(files, Environment.GetEnvironmentVariable), new YamlManifestReader(files))
            .ResolveAsync(workspace, options, CancellationToken.None);
        var process = runner.Start(LanguageServerCommandBuilder.Build(sdk, workspace, options));

        var settings = new LspAdapterSettings();
        var tracker = new InitializeTracker();
        var documents = new OpenDocuments();
        var transcript = new LspTranscriptRecorder(new TranscriptRedactor(
        [
            (root, "${ROOT}"),
            (Path.GetTempPath(), "${TEMP}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "${HOME}"),
        ]));
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            transcript.Outgoing(new ClientToServerStream(process.StandardInput, settings, tracker, NullDamlLogger.Instance, documents)),
            transcript.Incoming(new ServerToClientStream(process.StandardOutput, settings, tracker, NullDamlLogger.Instance, documents)),
            new JsonMessageFormatter()));
        var session = new RealDamlcSession(process, rpc, sdk, workspace, transcript, scenario, sample.SdkVersion);
        rpc.AddLocalRpcMethod("textDocument/publishDiagnostics", new Action<JToken>(session.OnDiagnostics));
        rpc.AddLocalRpcMethod("window/workDoneProgress/create", new Func<JToken, object?>(_ => null));
        rpc.AddLocalRpcMethod("client/registerCapability", new Func<JToken, object?>(_ => null));
        rpc.AddLocalRpcMethod("workspace/configuration", new Func<JToken, object?>(_ => new JArray()));
        // The extension's own handler for daml/* notifications, passing virtual resources on to
        // whatever a test plugs in.
        rpc.AddLocalRpcTarget(new DamlCustomMessageTarget(session.VirtualResources, new NoInstalls(), NullDamlLogger.Instance));
        rpc.StartListening();

        var initialize = rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new
        {
            processId = CurrentProcessId,
            rootUri = new Uri(workspace.WorkspaceRoot).AbsoluteUri,
            capabilities = ClientCapabilities,
        });
        if (await Task.WhenAny(initialize, Task.Delay(Patience)) != initialize)
        {
            await session.DisposeAsync();
            throw new TimeoutException($"damlc {mode} ({sdk.ContextSdkVersion}) didn't answer initialize.");
        }
        session.Capabilities = (JObject?)(await initialize)["capabilities"] ?? new JObject();
        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        return session;
    }

    public static string Uri(string path) => new Uri(path).AbsoluteUri;

#if NET
    private static int CurrentProcessId => Environment.ProcessId;
#else
    private static int CurrentProcessId => System.Diagnostics.Process.GetCurrentProcess().Id;
#endif

    public Task OpenAsync(string path) =>
        Rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = Uri(path), languageId = "daml", version = 1, text = File.ReadAllText(path) },
        });

    /// <summary>Waits for diagnostics on <paramref name="path"/> that satisfy <paramref name="accept"/>; returns them, or null on timeout.</summary>
    public async Task<JArray?> WaitForDiagnosticsAsync(string path, Func<JArray, bool> accept)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            lock (_diagnostics)
            {
                if (_diagnostics.TryGetValue(Key(Uri(path)), out var published) && published.LastOrDefault(accept) is { } match)
                {
                    return match;
                }
            }
            await Task.Delay(100);
        }
        return null;
    }

    public Task<JToken?> RequestAsync(string method, object parameters)
    {
        var request = Rpc.InvokeWithParameterObjectAsync<JToken?>(method, parameters);
        return WithPatienceAsync(request, method);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var shutdown = Rpc.InvokeAsync<object?>("shutdown");
            await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(5)));
            await Rpc.NotifyAsync("exit");
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or RemoteInvocationException)
        {
        }
        Rpc.Dispose();
        await Task.WhenAny(_stderr, Task.Delay(TimeSpan.FromSeconds(5)));
        _process.Dispose();
        SaveTranscript();
    }

    private void SaveTranscript()
    {
        var sdk = _sdkVersion;
        var name = $"{(Workspace.Mode == ServerMode.Ide ? "ide" : "multi-ide")}-{_scenario}.jsonl";
        _transcript.Save(Path.Combine(Repository.Root, "artifacts", "test", "lsp-transcripts", sdk, name));
        if (Environment.GetEnvironmentVariable("DAML_RECORD_TRANSCRIPTS") == "1")
        {
            _transcript.Save(Path.Combine(Repository.Root, "test", "fixtures", "lsp-transcripts", sdk, name));
        }
    }

    private static async Task<JToken?> WithPatienceAsync(Task<JToken?> request, string method)
    {
        if (await Task.WhenAny(request, Task.Delay(Patience)) != request)
        {
            throw new TimeoutException($"damlc didn't answer {method}.");
        }
        return await request;
    }

    // Some servers escape the drive colon in URIs; compare them unescaped.
    private static string Key(string uri) => System.Uri.UnescapeDataString(uri);

    private void OnDiagnostics(JToken parameters)
    {
        var uri = (string?)parameters["uri"];
        if (uri is null || parameters["diagnostics"] is not JArray diagnostics)
        {
            return;
        }
        lock (_diagnostics)
        {
            if (!_diagnostics.TryGetValue(Key(uri), out var published))
            {
                _diagnostics[Key(uri)] = published = new List<JArray>();
            }
            published.Add(diagnostics);
        }
    }

    // A reduced version of what Visual Studio 2026 announces; the adapter cleans it like VS's.
    private static readonly object ClientCapabilities = new
    {
        textDocument = new
        {
            synchronization = new { didSave = true, dynamicRegistration = false },
            hover = new { contentFormat = new[] { "markdown", "plaintext" } },
            completion = new { completionItem = new { snippetSupport = true }, completionItemKind = new { valueSet = Enumerable.Range(1, 25).ToArray() } },
            definition = new { linkSupport = false },
            documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
            codeAction = new { codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "quickfix", "refactor" } } } },
            codeLens = new { },
            publishDiagnostics = new { relatedInformation = true, versionSupport = true, tagSupport = new { valueSet = new[] { 1, 2 } } },
        },
        workspace = new { configuration = true, didChangeWatchedFiles = new { dynamicRegistration = true } },
        window = new { workDoneProgress = true },
    };

    /// <summary>Where the session's virtual resource notifications go; nowhere until a test sets <see cref="ForwardingVirtualResources.Target"/>.</summary>
    public ForwardingVirtualResources VirtualResources { get; } = new();

    internal sealed class ForwardingVirtualResources : IVirtualResourceSink
    {
        public IVirtualResourceSink? Target { get; set; }

        public void Changed(VirtualResourceChangedParams change) => Target?.Changed(change);

        public void Noted(VirtualResourceNoteParams note) => Target?.Noted(note);

        public void Progressed(VirtualResourceProgressParams progress) => Target?.Progressed(progress);
    }

    private sealed class NoInstalls : ISdkInstallProgressSink
    {
        public void Report(SdkInstallProgressParams progress)
        {
        }
    }
}
