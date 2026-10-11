using System;

namespace Daml.VisualStudio.Build;

// Identifiers of the Open Folder build integration.
internal static class BuildIds
{
    public const string FileContextProviderString = "202da85d-ff38-4820-b89e-d9f8db6d85b3";
    public const string ActionProviderString = "93136e07-9dde-4dd2-99f4-d51cc8feff4d";
    public const string FileScannerString = "c7b1c3a4-1d5e-4b8f-9a62-3f0e8d2b7a15";

    // Build and Clean use the built-in context types; Test has none, so it gets its own type.
    // Open Folder lists actions of such custom types in the folder-view menu by their display name.
    public const string TestContextTypeString = "23dcfaf1-d146-4c14-8aa7-67eef823a4ab";
    public static readonly Guid TestContextType = new(TestContextTypeString);

    // The folder view's own Build and Clean commands. An action has to name one of them to be
    // what those menu entries run.
    public static readonly Guid VsBuildCommandGroup = new("16537f6e-cb14-44da-b087-d1387ce3bf57");
    public const uint BuildCommandId = 0x1000;
    public const uint CleanCommandId = 0x1020;

    public static readonly Guid FileContextProvider = new(FileContextProviderString);
}
