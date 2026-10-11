using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Daml.TestHelpers;

/// <summary>What the fake language server answers.</summary>
public sealed class FakeLanguageServerScenario
{
    /// <summary>Returned as <c>capabilities</c> from <c>initialize</c>.</summary>
    public JObject Capabilities { get; set; } = new();

    /// <summary>Written to stderr, one per line, as soon as the server starts.</summary>
    public List<string> StderrLines { get; } = new();

    /// <summary>When set, the server exits with this code right after writing <see cref="StderrLines"/>.</summary>
    public int? ExitCodeAtStart { get; set; }

    /// <summary>When true, <c>initialize</c> never gets a reply, like a server stuck while starting.</summary>
    public bool HangOnInitialize { get; set; }
}
