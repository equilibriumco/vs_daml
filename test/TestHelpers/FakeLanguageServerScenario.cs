using Newtonsoft.Json.Linq;

namespace Daml.TestHelpers;

/// <summary>What the fake language server answers.</summary>
public sealed class FakeLanguageServerScenario
{
    /// <summary>Returned as <c>capabilities</c> from <c>initialize</c>.</summary>
    public JObject Capabilities { get; set; } = new();
}
