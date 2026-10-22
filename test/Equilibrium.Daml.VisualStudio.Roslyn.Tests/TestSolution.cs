using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Equilibrium.Daml.VisualStudio.Roslyn.Tests;

/// <summary>
/// An <see cref="AdhocWorkspace"/> with C# projects compiled as net10.0 projects referencing the
/// Daml runtime, the way Visual Studio's workspace holds them.
/// </summary>
internal sealed class TestSolution : IDisposable
{
    private readonly AdhocWorkspace _workspace = new();

    public Solution Solution { get; private set; }

    public TestSolution() => Solution = _workspace.CurrentSolution;

    public void Dispose() => _workspace.Dispose();

    /// <summary>Adds a project with <paramref name="files"/> and returns its id; without the Daml runtime when asked.</summary>
    public ProjectId Add(string name, IEnumerable<(string Path, string Text)> files, IEnumerable<MetadataReference>? references = null, bool withDamlRuntime = true)
    {
        var id = ProjectId.CreateNewId(name);
        Solution = Solution.AddProject(ProjectInfo.Create(
            id, VersionStamp.Create(), name, name.Split(' ')[0], LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: GeneratedCode.ParseOptions,
            metadataReferences: GeneratedCode.References.Where(r => withDamlRuntime || !Path.GetFileName(r.Display ?? "").StartsWith("Daml.", StringComparison.Ordinal)).Concat(references ?? [])));
        foreach (var (path, text) in files)
        {
            Solution = Solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(path), SourceText.From(text), filePath: path);
        }
        return id;
    }

    /// <summary>Lets the solution change in other ways.</summary>
    public void Change(Func<Solution, Solution> change) => Solution = change(Solution);

    /// <summary>The workspace, for whoever follows its changes.</summary>
    public Workspace Workspace => _workspace;

    /// <summary>Makes the changes so far the workspace's, which tells its listeners.</summary>
    public void Apply()
    {
        if (!_workspace.TryApplyChanges(Solution))
        {
            throw new InvalidOperationException("The workspace didn't take the changes.");
        }
        Solution = _workspace.CurrentSolution;
    }
}
