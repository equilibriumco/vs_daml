using System.Collections.Generic;

namespace Daml.TestHelpers;

/// <summary>What the fake dpm answers. Commands are tried in order; the first match wins.</summary>
public sealed class FakeDpmScenario
{
    /// <summary>Scripted commands.</summary>
    public List<FakeDpmCommand> Commands { get; } = new();
}
