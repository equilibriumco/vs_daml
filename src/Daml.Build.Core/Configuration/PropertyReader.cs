using System;
using System.Collections.Generic;
using System.Globalization;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Configuration;

// Reads project properties, recording an error for each value it can't use and carrying on with
// the default, so one build reports every bad setting at once.
internal sealed class PropertyReader(Func<string, string?> lookup, ICollection<ConfigurationError> errors)
{
    public string? Text(string name)
    {
        var value = lookup(name)?.Trim();
        return value is null || value.Length == 0 ? null : value;
    }

    public string? Text(DamlProperty property) => Text(property.Name);

    public string Required(string name) =>
        Text(name) ?? throw new ArgumentException($"MSBuild property {name} has no value.");

    public bool Bool(DamlProperty property)
    {
        var fallback = MSBuildValue.Bool(property.Default, false) ?? false;
        var text = lookup(property.Name);
        if (MSBuildValue.Bool(text, fallback) is { } value)
        {
            return value;
        }
        Invalid(property, text);
        return fallback;
    }

    public int Seconds(DamlProperty property, int minimum)
    {
        var fallback = int.Parse(property.Default, CultureInfo.InvariantCulture);
        var text = Text(property);
        if (text is null)
        {
            return fallback;
        }
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= minimum)
        {
            return value;
        }
        Invalid(property, text);
        return fallback;
    }

    public void Invalid(DamlProperty property, string? value) =>
        Error(DamlDiagnosticCodes.InvalidConfiguration, $"{property.Name} is '{value}', but it has to be {property.Allowed}.");

    public void Error(string code, string message) => errors.Add(new ConfigurationError(code, message));
}
