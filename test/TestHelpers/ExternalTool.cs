namespace Equilibrium.Daml.TestHelpers;

/// <summary>External programs that some tests need installed.</summary>
public enum ExternalTool
{
    /// <summary>Digital Asset's package manager, which also runs damlc.</summary>
    Dpm,

    /// <summary>A JDK; the C# code generator and the script service run on it.</summary>
    Java,

    /// <summary>MSBuild.exe of Visual Studio 2026 (18.x), one of the hosts the MSBuild packages support.</summary>
    MSBuild,

    /// <summary>The dotnet host, whose MSBuild is the other host the packages support.</summary>
    Dotnet,
}
