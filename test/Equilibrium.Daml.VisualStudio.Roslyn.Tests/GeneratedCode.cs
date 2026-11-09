using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Equilibrium.Daml.TestHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Tests;

/// <summary>
/// Generated C# from the checked-in fixtures, compiled the way a net10.0 project referencing the
/// Daml runtime compiles it: against the .NET 10 reference assemblies and Daml.Runtime and
/// Daml.Ledger.Abstractions 0.8.0, which the test project downloads as plain files.
/// </summary>
internal static class GeneratedCode
{
    /// <summary>The generator's output for the kinds fixture: the C# of a <c>Namespace="Fixture"</c> reference.</summary>
    public static string Kinds { get; } = Path.Combine(Repository.Root, @"test\fixtures\bindings\kinds\csharp");

    /// <summary>What the pinned generator writes for the mini demo, whose templates implement Splice interfaces.</summary>
    public static string MiniDemo { get; } = Path.Combine(Repository.Root, @"test\fixtures\bindings\mini-demo\csharp");

    public const string KindsPackage = "f212e3063732400d22fefbd51b4293366a542fddb4db3a16b14cc037486ce716";

    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly Lazy<IReadOnlyList<MetadataReference>> LazyReferences = new(LoadReferences);

    /// <summary>The .NET 10 reference assemblies and the Daml runtime.</summary>
    public static IReadOnlyList<MetadataReference> References => LazyReferences.Value;

    /// <summary>Every C# file under <paramref name="folder"/>, by full path.</summary>
    public static IReadOnlyList<(string Path, string Text)> Files(string folder) =>
        Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f))).ToList();

    /// <summary>A library compilation of <paramref name="files"/>, with or without the Daml runtime.</summary>
    public static CSharpCompilation Compile(IEnumerable<(string Path, string Text)> files, bool withDamlRuntime = true) =>
        CSharpCompilation.Create(
            "App",
            files.Select(f => CSharpSyntaxTree.ParseText(f.Text, ParseOptions, f.Path)),
            withDamlRuntime ? References : References.Where(r => !Path.GetFileName(r.Display ?? "").StartsWith("Daml.", StringComparison.Ordinal)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static List<MetadataReference> LoadReferences()
    {
        var packages = typeof(GeneratedCode).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "NuGetPackageRoot").Value
            ?? throw new InvalidOperationException("The test assembly doesn't know the NuGet package folder.");
        string[] folders =
        [
            Path.Combine(packages, @"microsoft.netcore.app.ref\10.0.0\ref\net10.0"),
            Path.Combine(packages, @"daml.runtime\0.8.0\lib\net10.0"),
            Path.Combine(packages, @"daml.ledger.abstractions\0.8.0\lib\net10.0"),
        ];
        return folders.SelectMany(f => Directory.GetFiles(f, "*.dll")).Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
    }
}
