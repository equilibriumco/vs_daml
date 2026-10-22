namespace Equilibrium.Daml.Build.Core.Diagnostics;

/// <summary>
/// The codes Daml build steps report. The extension reports the same codes for the same
/// problems, so a code means one thing wherever it shows up.
/// </summary>
public static class DamlDiagnosticCodes
{
    /// <summary>The Daml compiler reported an error.</summary>
    public const string CompileError = "EQD0001";

    /// <summary>The Daml compiler reported a warning.</summary>
    public const string CompileWarning = "EQD0002";

    /// <summary>A property, item, path or <c>daml.yaml</c> setting the build can't use.</summary>
    public const string InvalidConfiguration = "EQD0100";

    /// <summary>dpm, or the Daml SDK a package asks for, isn't installed.</summary>
    public const string MissingTool = "EQD0101";

    /// <summary>No JDK, or one older than the code generator needs.</summary>
    public const string MissingJava = "EQD0102";

    /// <summary>dpm is older than this release supports.</summary>
    public const string DpmTooOld = "EQD0103";

    /// <summary>The C# code generator failed, had no DAR to read, or left incomplete output.</summary>
    public const string CodegenFailed = "EQD0104";

    /// <summary>Another build held a lock for longer than this one would wait.</summary>
    public const string LockTimeout = "EQD0105";

    /// <summary>
    /// The generator isn't pinned by digest, isn't the one this release was tested with, or its
    /// cached copy doesn't match the pin and can't be repaired.
    /// </summary>
    public const string GeneratorPinConflict = "EQD0106";

    /// <summary>An offline build needs a tool or component that isn't installed.</summary>
    public const string MissingToolOffline = "EQD0107";

    /// <summary>Generated output would overwrite, delete or share files it doesn't own.</summary>
    public const string OutputConflict = "EQD0108";

    /// <summary>A failed publish couldn't put the previous output back; it's kept for the next build to recover.</summary>
    public const string RollbackFailed = "EQD0109";

    /// <summary>A tool was cancelled or ran past its time limit.</summary>
    public const string CancelledOrTimedOut = "EQD0110";

    /// <summary>
    /// A warning: a generated file's path is longer than Windows takes without long paths turned
    /// on, so the compiler or the editor may not open it.
    /// </summary>
    public const string LongGeneratedPath = "EQD0111";

    /// <summary>
    /// The project has no Daml runtime the generated code works with, or targets a framework the
    /// runtime doesn't support.
    /// </summary>
    public const string RuntimeMismatch = "EQD0201";

    /// <summary>Two references would generate the same C# types; a Namespace on one of them tells them apart.</summary>
    public const string TypeCollision = "EQD0202";

    /// <summary>Navigation between C# and Daml is unavailable: the Daml sources are ambiguous or out of date.</summary>
    public const string NavigationUnavailable = "EQD0203";
}
