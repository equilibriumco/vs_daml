using Xunit;

namespace Equilibrium.Daml.Build.Tests.Processes;

// Tests that change this process's environment, which every process another test starts at the
// same time would inherit: they run on their own, after the others.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironment
{
    public const string Name = "Process environment";
}
