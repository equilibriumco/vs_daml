using System;
using System.IO;

namespace Equilibrium.Daml.VisualStudio;

/// <summary>
/// Where the extension keeps what it writes for itself. Uninstalling can't run code, so this
/// folder stays until the user deletes it.
/// </summary>
internal static class ExtensionFolders
{
    /// <summary><c>%LOCALAPPDATA%\DamlVisualStudio</c>.</summary>
    public static string Data { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamlVisualStudio");

    /// <summary>Short copies of SDK library sources, which Go To Definition opens instead of paths too long for Visual Studio.</summary>
    public static string LibraryCopies { get; } = Path.Combine(Data, "library");
}
