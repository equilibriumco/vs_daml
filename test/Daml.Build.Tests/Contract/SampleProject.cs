using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Daml.Build.Core.Codegen;
using Daml.TestHelpers;

namespace Daml.Build.Tests.Contract;

/// <summary>
/// A sample project from <c>test/fixtures/msbuild-projects</c>, read as written: no evaluation,
/// so the samples have no conditions or imports. Every <c>ProjectReference</c> in them is to a
/// Daml project; the real build only learns that by asking the project.
/// </summary>
internal sealed class SampleProject
{
    private SampleProject(string name, IReadOnlyDictionary<string, string> properties, IReadOnlyList<DeclaredDamlReference> references)
    {
        Name = name;
        Properties = properties;
        References = references;
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public IReadOnlyList<DeclaredDamlReference> References { get; }

    public static IReadOnlyList<SampleProject> All() =>
        Directory.GetFiles(Path.Combine(Repository.Root, "test", "fixtures", "msbuild-projects"), "*.csproj")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToList();

    private static SampleProject Load(string path)
    {
        var project = XDocument.Load(path).Root!;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in project.Elements("PropertyGroup").Elements())
        {
            properties[property.Name.LocalName] = property.Value;
        }
        var references = new List<DeclaredDamlReference>();
        foreach (var item in project.Elements("ItemGroup").Elements())
        {
            var include = (string?)item.Attribute("Include") ?? "";
            DeclaredDamlReference? reference = item.Name.LocalName switch
            {
                "DamlReference" => new(DamlReferenceSource.DamlReference, include, Metadata(item, "Namespace")),
                "DamlDar" => new(DamlReferenceSource.DamlDar, include, Metadata(item, "Namespace")),
                "ProjectReference" => new(DamlReferenceSource.ProjectReference, include, Metadata(item, "DamlNamespace")),
                _ => null,
            };
            if (reference is not null)
            {
                references.Add(reference);
            }
        }
        return new SampleProject(Path.GetFileName(path), properties, references);
    }

    private static string? Metadata(XElement item, string name) =>
        (string?)item.Attribute(name) ?? (string?)item.Element(name);
}
