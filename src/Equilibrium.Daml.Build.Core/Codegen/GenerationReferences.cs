using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>Turns the references a project declares into the generations it needs.</summary>
public static class GenerationReferences
{
    private const string DarExtension = ".dar";

    private static readonly Regex Namespace = new(
        @"^[\p{L}_][\p{L}\p{Nd}_]*(?:\.[\p{L}_][\p{L}\p{Nd}_]*)*\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves each reference against the project, checks it, and gives it its identity. A Daml
    /// project listed both as a <c>EqDamlReference</c> and as a plain <c>ProjectReference</c> is one
    /// reference, described by the <c>EqDamlReference</c>. A reference declared twice with the same
    /// namespace would write the same output twice and is an error.
    /// </summary>
    /// <param name="declared">The references, in the order the project declares them; plain project references only when they are Daml projects.</param>
    /// <param name="projectDirectory">The full path of the C# project's folder.</param>
    /// <param name="generatorDigest">The pinned generator's digest.</param>
    /// <param name="errors">Gets an error for each reference that can't be used; such references are left out.</param>
    /// <returns>The generations, in declaration order.</returns>
    public static IReadOnlyList<GenerationReference> Resolve(
        IEnumerable<DeclaredDamlReference> declared,
        string projectDirectory,
        string generatorDigest,
        ICollection<ConfigurationError> errors)
    {
        if (declared is null)
        {
            throw new ArgumentNullException(nameof(declared));
        }
        if (projectDirectory is null)
        {
            throw new ArgumentNullException(nameof(projectDirectory));
        }
        if (errors is null)
        {
            throw new ArgumentNullException(nameof(errors));
        }
        var candidates = new List<GenerationReference>();
        var explicitProjects = new HashSet<string>(DamlPath.Comparer);
        foreach (var reference in declared)
        {
            if (Check(reference, projectDirectory, generatorDigest, errors) is not { } resolved)
            {
                continue;
            }
            candidates.Add(resolved);
            if (reference.Source == DamlReferenceSource.EqDamlReference)
            {
                explicitProjects.Add(resolved.Path);
            }
        }

        var result = new List<GenerationReference>();
        var byId = new Dictionary<string, GenerationReference>(StringComparer.Ordinal);
        foreach (var reference in candidates)
        {
            if (reference.Source == DamlReferenceSource.ProjectReference && explicitProjects.Contains(reference.Path))
            {
                continue;
            }
            if (byId.TryGetValue(reference.GenerationId, out var first))
            {
                var how = reference.Namespace is null ? "without a Namespace" : $"with Namespace '{reference.Namespace}'";
                errors.Add(new ConfigurationError(
                    DamlDiagnosticCodes.OutputConflict,
                    $"{reference.Include} is referenced again {how} after {first.Include}, so both would generate into {first.FolderName}. Remove one of them."));
                continue;
            }
            byId.Add(reference.GenerationId, reference);
            result.Add(reference);
        }
        return result;
    }

    private static GenerationReference? Check(DeclaredDamlReference reference, string projectDirectory, string generatorDigest, ICollection<ConfigurationError> errors)
    {
        var path = DamlPath.Resolve(projectDirectory, reference.Include);
        var isDar = path.EndsWith(DarExtension, StringComparison.OrdinalIgnoreCase);
        if (reference.Source == DamlReferenceSource.EqDamlDar && !isDar)
        {
            Invalid(errors, $"EqDamlDar {reference.Include} isn't a .dar file. Reference a Daml project with EqDamlReference instead.");
            return null;
        }
        if (reference.Source != DamlReferenceSource.EqDamlDar && isDar)
        {
            Invalid(errors, $"{reference.Source} {reference.Include} is a DAR. Reference DAR files with EqDamlDar instead.");
            return null;
        }

        var @namespace = reference.Namespace?.Trim();
        if (@namespace is { Length: 0 })
        {
            @namespace = null;
        }
        if (@namespace is not null && !Namespace.IsMatch(@namespace))
        {
            Invalid(errors, $"Namespace '{@namespace}' on {reference.Include} isn't a C# namespace: use names made of letters, digits and underscores, joined by dots.");
            return null;
        }

        var kind = reference.Source == DamlReferenceSource.EqDamlDar ? DamlReferenceKind.Dar : DamlReferenceKind.Project;
        var id = GenerationIdentity.Id(kind, path, @namespace, generatorDigest);
        return new GenerationReference(reference.Source, reference.Include, path, @namespace, id, GenerationIdentity.FolderName(path, id));
    }

    private static void Invalid(ICollection<ConfigurationError> errors, string message) =>
        errors.Add(new ConfigurationError(DamlDiagnosticCodes.InvalidConfiguration, message));
}
