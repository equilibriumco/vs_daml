using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Configuration;

/// <summary>Where generated C# goes.</summary>
public enum GeneratedOutputMode
{
    /// <summary>Under the build's intermediate folder, one root per configuration and target framework.</summary>
    Intermediate,

    /// <summary>Into the project, to be committed with it.</summary>
    Source,
}

/// <summary>How a C# project generates bindings from DARs.</summary>
/// <param name="Enabled">Whether bindings are generated and compiled at all.</param>
/// <param name="Component">The pinned generator; null when the setting can't be used.</param>
/// <param name="OutputMode">Where generated code goes.</param>
/// <param name="GeneratedRoot">The canonical folder holding every generation; null when the setting can't be used.</param>
/// <param name="JavaHome">The JDK the generator runs on; null for java on PATH.</param>
public sealed record CodegenOptions(
    bool Enabled,
    CodegenComponentPin? Component,
    GeneratedOutputMode OutputMode,
    string? GeneratedRoot,
    string? JavaHome)
{
    /// <summary>Reads the options from a project's properties.</summary>
    /// <param name="property">
    /// A property's value by name; null or empty when unset. MSBuild's own
    /// <c>MSBuildProjectDirectory</c> and <c>IntermediateOutputPath</c> must have values.
    /// </param>
    /// <param name="errors">Gets an error for each value that can't be used.</param>
    /// <exception cref="ArgumentException">The project folder or the intermediate folder is unknown.</exception>
    public static CodegenOptions Read(Func<string, string?> property, ICollection<ConfigurationError> errors)
    {
        if (property is null)
        {
            throw new ArgumentNullException(nameof(property));
        }
        if (errors is null)
        {
            throw new ArgumentNullException(nameof(errors));
        }
        var reader = new PropertyReader(property, errors);
        var enabled = reader.Bool(DamlProperties.CodegenEnabled);

        var componentText = reader.Text(DamlProperties.CodegenComponent) ?? DamlProperties.CodegenComponent.Default;
        var component = CodegenComponentPin.TryParse(componentText);
        if (component is null)
        {
            reader.Error(
                DamlDiagnosticCodes.GeneratorPinConflict,
                $"{DamlProperties.CodegenComponent.Name} is '{componentText}', but the generator has to be pinned by its digest: {DamlProperties.CodegenComponent.Allowed}.");
        }

        var modeText = reader.Text(DamlProperties.GeneratedOutputMode) ?? DamlProperties.GeneratedOutputMode.Default;
        GeneratedOutputMode? mode =
            string.Equals(modeText, nameof(GeneratedOutputMode.Intermediate), StringComparison.OrdinalIgnoreCase) ? GeneratedOutputMode.Intermediate
            : string.Equals(modeText, nameof(GeneratedOutputMode.Source), StringComparison.OrdinalIgnoreCase) ? GeneratedOutputMode.Source
            : null;
        if (mode is null)
        {
            reader.Invalid(DamlProperties.GeneratedOutputMode, modeText);
        }

        return new CodegenOptions(
            enabled,
            component,
            mode ?? GeneratedOutputMode.Intermediate,
            mode is { } known ? Root(reader, known) : null,
            reader.Text(DamlProperties.JavaHome));
    }

    private static string? Root(PropertyReader reader, GeneratedOutputMode mode)
    {
        // MSBuild gives the project folder as a full path; the process's current folder means nothing in a build node.
        var project = DamlPath.Normalize(reader.Required("MSBuildProjectDirectory"));
        var intermediate = DamlPath.Resolve(project, reader.Required("IntermediateOutputPath"));
        var chosen = reader.Text(DamlProperties.GeneratedDir);
        var root = DamlPath.Resolve(
            project,
            chosen ?? (mode == GeneratedOutputMode.Source ? @"Generated\Daml" : Path.Combine(intermediate, "Daml")));

        var name = DamlProperties.GeneratedDir.Name;
        if (DamlPath.IsRoot(root) || DamlPath.IsSameOrUnder(project, root))
        {
            reader.Error(
                DamlDiagnosticCodes.InvalidConfiguration,
                $"{name} is '{chosen}', which holds the project folder {project}. Generated code needs a folder of its own.");
            return null;
        }
        // Each configuration and target framework generates on its own in intermediate mode;
        // only a folder inside that build's intermediate folder keeps them apart.
        if (mode == GeneratedOutputMode.Intermediate && (!DamlPath.IsSameOrUnder(root, intermediate) || root.Length == intermediate.Length))
        {
            reader.Error(
                DamlDiagnosticCodes.InvalidConfiguration,
                $"{name} is '{chosen}', but in Intermediate mode it has to be a folder inside the build's intermediate folder {intermediate}. Use Source mode to put generated code elsewhere.");
            return null;
        }
        // Every inner build of a multi-targeted project would publish into the one Source folder.
        var frameworks = reader.Text("TargetFrameworks");
        if (mode == GeneratedOutputMode.Source && frameworks is not null && frameworks.Split(';').Count(f => f.Trim().Length > 0) > 1)
        {
            reader.Error(
                DamlDiagnosticCodes.OutputConflict,
                $"The project targets several frameworks ({frameworks}), but generates its Daml bindings into {root} (Source mode), which only one of them can own. " +
                $"Target a single framework, or set {DamlProperties.GeneratedOutputMode.Name} to Intermediate.");
        }
        return root;
    }
}
