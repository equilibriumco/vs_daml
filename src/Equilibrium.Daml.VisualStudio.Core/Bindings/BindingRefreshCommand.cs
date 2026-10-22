using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Equilibrium.Daml.VisualStudio.Core.Bindings;

/// <summary>
/// The MSBuild run behind a background refresh: the targeted build <c>EqDamlRefreshBindings</c> of the
/// C# project in the configuration Visual Studio builds it in. References give their DARs, but no
/// C# library is compiled (<c>BuildProjectReferences=false</c>), and installing tools stays up to the
/// project's own <c>EqDamlAllowToolInstall</c>.
/// </summary>
public static class BindingRefreshCommand
{
    /// <summary>MSBuild's arguments for refreshing <paramref name="target"/>.</summary>
    /// <param name="target">The project, and its target framework when it builds for several.</param>
    /// <param name="configuration">The project configuration, such as Debug.</param>
    /// <param name="platform">The MSBuild platform, such as AnyCPU.</param>
    /// <param name="solutionDirectory">The solution's folder, which builds in Visual Studio know as SolutionDir; null without one.</param>
    public static IReadOnlyList<string> Arguments(BindingRefreshTarget target, string configuration, string platform, string? solutionDirectory)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }
        var arguments = new List<string>
        {
            target.Project,
            "-t:EqDamlRefreshBindings",
            "-p:Configuration=" + configuration,
            "-p:Platform=" + platform,
            "-p:BuildProjectReferences=false",
        };
        if (solutionDirectory is not null)
        {
            arguments.Add("-p:SolutionDir=" + solutionDirectory.TrimEnd('\\') + "\\");
        }
        if (target.TargetFramework is { } framework)
        {
            arguments.Add("-p:TargetFramework=" + framework);
        }
        // Errors once each, without the summary that repeats them.
        arguments.AddRange(["-nologo", "-nodeReuse:false", "-verbosity:minimal", "-consoleLoggerParameters:NoSummary"]);
        return arguments;
    }

    /// <summary>
    /// The configuration and MSBuild platform of a project configuration as Visual Studio names it
    /// (<c>Debug|Any CPU</c>); MSBuild spells that platform <c>AnyCPU</c>, and a name without a
    /// platform is AnyCPU.
    /// </summary>
    public static (string Configuration, string Platform) Configuration(string canonicalName)
    {
        if (canonicalName is null)
        {
            throw new ArgumentNullException(nameof(canonicalName));
        }
        var parts = canonicalName.Split('|');
        var platform = parts.Length > 1 ? parts[1].Trim() : "";
        return (parts[0].Trim(), platform.Length == 0 || platform.Equals("Any CPU", StringComparison.OrdinalIgnoreCase) ? "AnyCPU" : platform);
    }

    /// <summary>
    /// The 64-bit MSBuild.exe of the Visual Studio installed in <paramref name="installDirectory"/>
    /// (its <c>Common7\IDE</c>): the one Visual Studio builds with, and free of the 32-bit one's path limits.
    /// </summary>
    public static string MSBuildOf(string installDirectory) =>
        Path.GetFullPath(Path.Combine(installDirectory, "..", "..", "MSBuild", "Current", "Bin", "amd64", "MSBuild.exe"));
}

/// <summary>An error or warning MSBuild printed.</summary>
/// <param name="File">The file it is in, or the project for one without a place.</param>
/// <param name="Line">The line; 0 when none is given.</param>
/// <param name="Column">The column; 0 when none is given.</param>
/// <param name="IsError">An error rather than a warning.</param>
/// <param name="Code">Its code, such as DAML0001.</param>
/// <param name="Message">What it says.</param>
public sealed record MSBuildDiagnostic(string File, int Line, int Column, bool IsError, string Code, string Message);

/// <summary>Reads the errors and warnings out of what MSBuild printed to the console.</summary>
public static class MSBuildOutput
{
    // file(line[,column[,endLine,endColumn]]): error CODE: message [project]
    private static readonly Regex Canonical = new(
        @"^\s*(?<file>.+?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>The diagnostics in <paramref name="output"/>, each once, in the order printed.</summary>
    public static IReadOnlyList<MSBuildDiagnostic> Diagnostics(string output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Canonical.Match(line))
            .Where(m => m.Success)
            .Select(m => new MSBuildDiagnostic(
                m.Groups["file"].Value.Trim(),
                Number(m.Groups["line"]),
                Number(m.Groups["column"]),
                m.Groups["severity"].Value == "error",
                m.Groups["code"].Value,
                m.Groups["message"].Value))
            .Distinct()
            .ToList();
    }

    private static int Number(Group group) =>
        group.Success ? int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
}
