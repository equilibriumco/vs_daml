using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Daml.TestHelpers;

/// <summary>What the fake language server answers.</summary>
public sealed class FakeLanguageServerScenario
{
    /// <summary>
    /// Returned as <c>capabilities</c> from <c>initialize</c>: what damlc 3.6.0 (ide) announced
    /// when the capabilities fixtures were recorded, unless a test says otherwise.
    /// </summary>
    public JObject Capabilities { get; set; } = RecordedCapabilities("ide-3.6.0");

    /// <summary>Written to stderr, one per line, as soon as the server starts.</summary>
    public List<string> StderrLines { get; } = new();

    /// <summary>Written to stderr, one per line, once the client has said <c>initialized</c>.</summary>
    public List<string> StderrLinesAfterInitialized { get; } = new();

    /// <summary>When set, the server exits with this code right after writing <see cref="StderrLines"/>.</summary>
    public int? ExitCodeAtStart { get; set; }

    /// <summary>When true, <c>initialize</c> never gets a reply, like a server stuck while starting.</summary>
    public bool HangOnInitialize { get; set; }

    /// <summary>When true, <c>daml/keepAlive</c> never gets a reply, like a server that hangs.</summary>
    public bool HangOnKeepAlive { get; set; }

    /// <summary>Notifications the server sends, in order, once the client says <c>initialized</c>.</summary>
    public List<JObject> NotificationsAfterInitialized { get; } = new();

    /// <summary>
    /// A recorded transcript (<c>test/fixtures/lsp-transcripts</c>) to answer from: each client
    /// message is matched to the next recorded one with the same method and gets what the real
    /// damlc sent then. Overrides <see cref="Capabilities"/> and the notifications.
    /// </summary>
    public string? Replay { get; set; }

    /// <summary>What <c>${ROOT}</c> in the transcript stands for on replay, as a path with forward slashes.</summary>
    public string? ReplayRoot { get; set; }

    /// <summary>The capabilities damlc announced when the fixture was recorded; empty when the fixture is missing.</summary>
    public static JObject RecordedCapabilities(string name)
    {
        var path = Path.Combine(Repository.Root, "test", "fixtures", "lsp-capabilities", name + ".json");
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
    }

    /// <summary>Full path of a recorded transcript, by SDK version and name.</summary>
    public static string Transcript(string sdkVersion, string name) =>
        Path.Combine(Repository.Root, "test", "fixtures", "lsp-transcripts", sdkVersion, name + ".jsonl");
}
