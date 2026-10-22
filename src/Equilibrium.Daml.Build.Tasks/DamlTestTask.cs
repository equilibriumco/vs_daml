using Equilibrium.Daml.Build.Core;

namespace Equilibrium.Daml.Build.Tasks;

/// <summary>Runs a package's Daml Script tests with <c>dpm test</c>; a failing script is an error at the script.</summary>
public sealed class DamlTestTask : DpmToolTask
{
    /// <inheritdoc />
    protected override DpmCommandKind Command => DpmCommandKind.Test;
}
