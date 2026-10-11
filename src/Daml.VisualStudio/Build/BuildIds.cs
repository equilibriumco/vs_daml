using System;

namespace Daml.VisualStudio.Build;

// Identifiers of the Open Folder build integration. The command set and command id must match
// DamlPackage.vsct.
internal static class BuildIds
{
    public const string FileContextProviderString = "202da85d-ff38-4820-b89e-d9f8db6d85b3";
    public const string ActionProviderString = "93136e07-9dde-4dd2-99f4-d51cc8feff4d";

    // Build and Clean use the built-in context types, which put them in the folder-view menu by
    // themselves; Test has no built-in type, so it gets its own type and menu command.
    public const string TestContextTypeString = "23dcfaf1-d146-4c14-8aa7-67eef823a4ab";
    public static readonly Guid TestContextType = new(TestContextTypeString);

    public const string CommandSetString = "9387515d-2cd9-4b38-87ba-e7d2f53a9d0d";
    public static readonly Guid CommandSet = new(CommandSetString);
    public const int TestCommandId = 0x0100;

    public static readonly Guid FileContextProvider = new(FileContextProviderString);
}
