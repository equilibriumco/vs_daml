namespace Daml.VisualStudio;

/// <summary>What Help > About and the installed-product registration say about the extension.</summary>
internal static partial class ProductInfo
{
    public const string Name = "EQ Daml Tools for Visual Studio";

    public const string Description =
        "Daml language support, project system and C# integration for Visual Studio. " +
        "Independent Daml tooling by Equilibrium — not affiliated with or endorsed by Digital Asset. " +
        "Daml is a trademark of Digital Asset (Switzerland) GmbH.";

    // Version is generated from the build's VersionPrefix; see the project file.
}
