namespace Equilibrium.Daml.Build.Core.Configuration;

/// <summary>Property values read the way MSBuild reads them.</summary>
public static class MSBuildValue
{
    /// <summary>
    /// A boolean as MSBuild accepts it: <c>true</c>, <c>on</c> or <c>yes</c>, <c>false</c>,
    /// <c>off</c> or <c>no</c>, any of them negated with a leading <c>!</c>, in any letter case.
    /// </summary>
    /// <returns><paramref name="defaultValue"/> when the text is empty; null when it isn't a boolean.</returns>
    public static bool? Bool(string? text, bool defaultValue)
    {
        var value = text?.Trim();
        if (value is null || value.Length == 0)
        {
            return defaultValue;
        }
        var negated = value[0] == '!';
        bool? parsed = (negated ? value.Substring(1) : value).ToLowerInvariant() switch
        {
            "true" or "on" or "yes" => true,
            "false" or "off" or "no" => false,
            _ => null,
        };
        return negated ? !parsed : parsed;
    }
}
