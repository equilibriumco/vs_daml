using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.Build.Core.Configuration;

/// <summary>How a Daml project builds its package.</summary>
/// <param name="BuildEnabled">Whether the build runs dpm at all; when off, the project still hands on the DAR it has.</param>
public sealed record PackageOptions(bool BuildEnabled)
{
    /// <summary>Reads the options from a Daml project's properties.</summary>
    /// <param name="property">A property's value by name; null or empty when unset.</param>
    /// <param name="errors">Gets an error for each value that can't be used.</param>
    public static PackageOptions Read(Func<string, string?> property, ICollection<ConfigurationError> errors)
    {
        if (property is null)
        {
            throw new ArgumentNullException(nameof(property));
        }
        if (errors is null)
        {
            throw new ArgumentNullException(nameof(errors));
        }
        return new PackageOptions(new PropertyReader(property, errors).Bool(DamlProperties.BuildEnabled));
    }
}
