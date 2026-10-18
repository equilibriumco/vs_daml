using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.FileSystem;
using Daml.VisualStudio.Core.Bindings;
using Daml.VisualStudio.Roslyn.Bindings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Daml.VisualStudio.Benchmarks;

/// <summary>
/// The binding index of a project with 500 generated Daml types: the kinds fixture published as 50
/// generations, each under a namespace of its own. Every iteration gets a new compilation, already
/// bound, as Visual Studio has one for IntelliSense after a build: the index pays for its own work
/// only. With a cold catalog it also reads every manifest and hashes every file, as when a solution
/// opens; with a warm one, only what changed.
/// </summary>
[MemoryDiagnoser]
public class BindingIndexBenchmarks : IDisposable
{
    private const string Package = "bd2aa85e017dad908dbd9c8103929df3edaa7b0961b675d3ef216644565f9cb1";
    private const int Generations = 50;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-index-bench-" + Guid.NewGuid().ToString("N"));
    private AdhocWorkspace? _workspace;
    private Solution? _solution;
    private DocumentId? _program;
    private readonly GenerationCatalog _warm = new();
    private GenerationCatalog _catalog = new();
    private int _iteration;

    /// <summary>Publishes the generations and opens them as a project.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var files = Enumerable.Range(0, Generations).SelectMany(i => Publish("Gen" + i)).ToList();
        _workspace = new AdhocWorkspace();
        var project = ProjectId.CreateNewId("App");
        _solution = _workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            project, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: References()));
        foreach (var (path, text) in files)
        {
            _solution = _solution.AddDocument(DocumentId.CreateNewId(project), Path.GetFileName(path), SourceText.From(text), filePath: path);
        }
        _program = DocumentId.CreateNewId(project);
        _solution = _solution.AddDocument(_program, "Program.cs", SourceText.From("internal static class Program { }"), filePath: Path.Combine(_root, @"App\Program.cs"));
#pragma warning disable VSTHRD002 // BenchmarkDotNet calls setups on its own thread, which has no synchronization context.
        if (Index(_warm).GetAwaiter().GetResult().Count == 0)
#pragma warning restore VSTHRD002
        {
            throw new InvalidOperationException("The generations gave no bindings.");
        }
    }

    /// <summary>A new, bound compilation for each iteration, and a catalog that hasn't read anything.</summary>
    [IterationSetup]
    public void NewCompilation()
    {
#pragma warning disable VSTHRD002 // BenchmarkDotNet calls setups on its own thread, which has no synchronization context.
        _iteration++;
        _solution = _solution!.WithDocumentText(_program!, SourceText.From($"internal static class Program {{ private static int _x{_iteration}; }}"));
        _solution.GetProject(_program!.ProjectId)!.GetCompilationAsync().GetAwaiter().GetResult()!.GetDiagnostics();
#pragma warning restore VSTHRD002
        _catalog = new GenerationCatalog();
    }

    /// <summary>The whole index, manifests read and files hashed.</summary>
    [Benchmark]
    public Task<BindingIndex> BuildWithColdCatalog() => Index(_catalog);

    /// <summary>The whole index, with the generations already known.</summary>
    [Benchmark]
    public Task<BindingIndex> BuildWithWarmCatalog() => Index(_warm);

    /// <summary>Removes the generations.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _workspace?.Dispose();
        _workspace = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Removes the generations, if the cleanup didnt.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Removes the generations.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cleanup();
        }
    }

    private Task<BindingIndex> Index(GenerationCatalog catalog) => BindingIndexBuilder.BuildAsync(_solution!, catalog);

    // The fixture's files under another namespace prefix, with a manifest, as a build publishes them.
    private List<(string Path, string Text)> Publish(string prefix)
    {
        var folder = Path.Combine(_root, @"App\obj\Debug\net10.0\Daml", "Contracts-" + Hashing.Sha256Text(prefix).Substring(0, 16));
        var written = new List<(string, string)>();
        var entries = new List<GenerationManifestFile>();
        var assembly = typeof(BindingIndexBenchmarks).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("kinds/", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var relative = prefix + resource.Substring("kinds/Fixture".Length).Replace('/', '\\');
            var path = Path.Combine(folder, relative);
            var text = reader.ReadToEnd().Replace("Fixture.Kinds", prefix + ".Kinds");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            written.Add((path, text));
            entries.Add(new GenerationManifestFile(relative, Hashing.Sha256File(path)));
        }
        new GenerationManifest(prefix, "f00d", Path.Combine(_root, @"App\App.csproj"), DamlReferenceKind.Project, Path.Combine(_root, @"Contracts\Contracts.msbuildproj"),
            prefix, Path.Combine(_root, @"Contracts\kinds-1.0.0.dar"), "da7a", "generator", entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList())
        {
            Package = new DarProvenance("kinds", "1.0.0", "3.6.0", Package, []),
        }.Write(Path.Combine(folder, GenerationManifest.FileName));
        return written;
    }

    // The .NET 10 reference assemblies and the Daml runtime, downloaded by the project as plain files.
    private static List<MetadataReference> References()
    {
        var packages = typeof(BindingIndexBenchmarks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "NuGetPackageRoot").Value!;
        string[] folders =
        [
            Path.Combine(packages, @"microsoft.netcore.app.ref\10.0.0\ref\net10.0"),
            Path.Combine(packages, @"daml.runtime\0.6.0\lib\net10.0"),
            Path.Combine(packages, @"daml.ledger.abstractions\0.6.0\lib\net10.0"),
        ];
        return folders.SelectMany(f => Directory.GetFiles(f, "*.dll")).Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
    }
}
