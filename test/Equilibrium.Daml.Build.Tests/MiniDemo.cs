using System.IO;
using Equilibrium.Daml.TestHelpers;

namespace Equilibrium.Daml.Build.Tests;

/// <summary>
/// The Daml package of the generator's mini demo, vendored in third_party. Upstream builds it on
/// SDK 3.5.2; copies are moved to the SDK the bindings fixture was recorded on, which also fixes
/// the package ID the generated code registers.
/// </summary>
internal static class MiniDemo
{
    public const string Sdk = "3.7.1";

    public const string PackageId = "b2fd028a37023d031efa04d0f1a06c88d5e5614083e3fa236f680f95f9a36bf2";

    public static string Daml { get; } = Path.Combine(Repository.Root, @"third_party\canton-dotnet-sdk-mini-demo\daml");

    /// <summary>What the pinned generator writes for the package, checked in.</summary>
    public static string Bindings { get; } = Path.Combine(Repository.Root, @"test\fixtures\bindings\mini-demo\csharp");

    /// <summary>Points the copied package's <c>daml.yaml</c> at <see cref="Sdk"/>.</summary>
    public static void UseFixtureSdk(string packageFolder)
    {
        var manifest = Path.Combine(packageFolder, "daml.yaml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("sdk-version: 3.5.2", "sdk-version: " + Sdk));
    }
}
