using System;
using System.Collections.Generic;
using Daml.Build.Core.Diagnostics;

namespace Daml.Build.Core.Configuration;

/// <summary>How Daml build steps run tools: which dpm, whether anything may be downloaded, how long to wait.</summary>
/// <param name="DpmPath">dpm as the project sets it; null to look for it.</param>
/// <param name="Offline">Never install or download anything; a missing tool is an error.</param>
/// <param name="AllowToolInstall">Let dpm install a missing Daml SDK or code generator.</param>
/// <param name="SkipPreflight">Skip the tool version checks, at the user's own risk.</param>
/// <param name="ProcessTimeout">How long one tool run may take; null for no limit.</param>
/// <param name="LockTimeout">How long to wait for another build's lock.</param>
public sealed record ToolOptions(
    string? DpmPath,
    bool Offline,
    bool AllowToolInstall,
    bool SkipPreflight,
    TimeSpan? ProcessTimeout,
    TimeSpan LockTimeout)
{
    /// <summary>
    /// The variables dpm runs with. <c>DPM_AUTO_INSTALL</c> is always set, so the user's own
    /// value never decides whether a build downloads anything; it is true only when installing
    /// is allowed and the build isn't offline.
    /// </summary>
    public IReadOnlyDictionary<string, string> DpmEnvironment => new Dictionary<string, string>
    {
        ["DPM_AUTO_INSTALL"] = AllowToolInstall && !Offline ? "true" : "false",
    };

    /// <summary>Reads the options from a project's properties.</summary>
    /// <param name="property">A property's value by name; null or empty when unset.</param>
    /// <param name="errors">Gets an error for each value that can't be used; such values fall back to their defaults.</param>
    public static ToolOptions Read(Func<string, string?> property, ICollection<ConfigurationError> errors)
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
        var offline = reader.Bool(DamlProperties.Offline);
        var install = reader.Bool(DamlProperties.AllowToolInstall);
        if (offline && install)
        {
            reader.Error(
                DamlDiagnosticCodes.InvalidConfiguration,
                $"{DamlProperties.Offline.Name} and {DamlProperties.AllowToolInstall.Name} are both true, but an offline build never installs anything. Turn one of them off.");
        }
        var process = reader.Seconds(DamlProperties.ProcessTimeoutSeconds, minimum: 0);
        return new ToolOptions(
            reader.Text(DamlProperties.DpmPath),
            offline,
            install,
            reader.Bool(DamlProperties.SkipPreflight),
            process == 0 ? null : TimeSpan.FromSeconds(process),
            TimeSpan.FromSeconds(reader.Seconds(DamlProperties.LockTimeoutSeconds, minimum: 1)));
    }
}
