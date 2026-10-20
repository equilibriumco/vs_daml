using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daml.Build.Core.Codegen;

namespace Daml.VisualStudio.Core.Bindings;

/// <summary>
/// The Daml projects a C# project generates from in one configuration, read from what its builds
/// left: the list of generated files for IntelliSense (<c>obj\daml\&lt;project&gt;.&lt;configuration&gt;.&lt;framework&gt;.g.props</c>)
/// and the manifests of those files' generations, which name the Daml sources. Nothing before the
/// project's first build, and nothing for a generation from a DAR file.
/// </summary>
public sealed class BindingConsumerReader
{
    private readonly GenerationCatalog _catalog;

    /// <summary>Creates the reader looking generations up in <paramref name="catalog"/>.</summary>
    public BindingConsumerReader(GenerationCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>The list of generated files a build of <paramref name="projectFile"/> leaves for one configuration and framework.</summary>
    public static string ListOf(string projectFile, string configuration, string framework) =>
        ListOf(projectFile, configuration, framework, "obj");

    /// <summary>The generated list under the evaluated BaseIntermediateOutputPath.</summary>
    public static string ListOf(string projectFile, string configuration, string framework, string intermediatePath) =>
        Path.Combine(Path.GetDirectoryName(projectFile ?? throw new ArgumentNullException(nameof(projectFile)))!, intermediatePath, "daml",
            $"{Path.GetFileName(projectFile)}.{configuration}.{framework}.g.props");

    /// <summary>What <paramref name="projectFile"/> generates from in <paramref name="configuration"/>, once per framework and Daml project.</summary>
    public IReadOnlyList<BindingConsumer> Read(string projectFile, string configuration) => Read(projectFile, configuration, "obj");

    /// <summary>Reads consumers under the evaluated BaseIntermediateOutputPath.</summary>
    public IReadOnlyList<BindingConsumer> Read(string projectFile, string configuration, string intermediatePath)
    {
        if (projectFile is null)
        {
            throw new ArgumentNullException(nameof(projectFile));
        }
        var lists = Path.Combine(Path.GetDirectoryName(projectFile)!, intermediatePath, "daml");
        if (!Directory.Exists(lists))
        {
            return [];
        }
        const string Suffix = ".g.props";
        var prefix = Path.GetFileName(projectFile) + "." + configuration + ".";
        var consumers = new List<BindingConsumer>();
        foreach (var list in Directory.EnumerateFiles(lists, prefix + "*" + Suffix))
        {
            var name = Path.GetFileName(list);
            var framework = name.Substring(prefix.Length, name.Length - prefix.Length - Suffix.Length);
            var generations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in GeneratedProps.Read(list))
            {
                // One file tells its whole generation.
                if (generations.Add(file.GenerationId)
                    && _catalog.Find(file.Path)?.Provenance is { ReferenceKind: DamlReferenceKind.Project, Source: { } source })
                {
                    var consumer = new BindingConsumer(projectFile, framework, source.ManifestPath);
                    if (!consumers.Contains(consumer))
                    {
                        consumers.Add(consumer);
                    }
                }
            }
        }
        return consumers;
    }
}
