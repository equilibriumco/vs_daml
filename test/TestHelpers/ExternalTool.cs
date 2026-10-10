namespace Daml.TestHelpers;

/// <summary>External programs that some tests need installed.</summary>
public enum ExternalTool
{
    /// <summary>Digital Asset's package manager, which also runs damlc.</summary>
    Dpm,

    /// <summary>A JDK; the C# code generator and the script service run on it.</summary>
    Java,
}
