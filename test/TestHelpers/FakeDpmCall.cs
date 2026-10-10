using System.Collections.Generic;

namespace Daml.TestHelpers;

/// <summary>One recorded invocation of the fake dpm.</summary>
public sealed class FakeDpmCall
{
    /// <summary>Creates a recorded call.</summary>
    public FakeDpmCall(IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        Args = args;
        WorkingDirectory = workingDirectory;
        Environment = environment;
    }

    /// <summary>Arguments as received.</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>Working directory of the process.</summary>
    public string WorkingDirectory { get; }

    /// <summary>The DPM_*, DAML_* and JAVA_HOME variables the process saw, keys upper-cased.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
}
