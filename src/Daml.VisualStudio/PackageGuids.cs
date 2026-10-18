using System;

namespace Daml.VisualStudio;

internal static class PackageGuids
{
    public const string PackageString = "117354e2-f560-49ff-8753-121bb9a6b227";

    // UI context that turns on when Daml content is in play; drives package autoload.
    public const string DamlActiveString = "fc87431c-653c-4483-a783-beb6e66e8cfe";

    // UI context that is active while a language server session is serving. Commands can key
    // their visibility on it, and the IDE tests wait on it instead of sleeping.
    public const string LanguageServerReadyString = "6cc58c93-db1a-42f9-999d-5f010a023df2";
    public static readonly Guid LanguageServerReady = new(LanguageServerReadyString);

    // Output panes. Fixed GUIDs so a pane is reused, not duplicated, across sessions.
    public static readonly Guid ExtensionPane = new("e591e6cb-c20e-4529-9bbd-95967aad0b88");
    public static readonly Guid LanguageServerPane = new("c7638d4a-ffd8-4a66-a687-38e6b7279eaf");
    public static readonly Guid BuildPane = new("78e76cd5-a3c0-4863-8f68-39be37ff1f4c");

    // The script results tool window type; each results resource gets its own instance of it.
    public const string ScriptResultsWindowString = "2c1e6e0b-7d43-4a7b-9b1e-5f2a8c6d4e91";

    // The "Daml: C# References" tool window; one instance, filled again by each lens click.
    public const string CSharpReferencesWindowString = "9171ec86-4323-4292-ba3a-bded468b24f5";
}
