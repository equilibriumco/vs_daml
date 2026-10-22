using System;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Codegen;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Equilibrium.Daml.Build.Tasks;

/// <summary>
/// Keeps the props file that lists a project's generated files for its evaluation, which is all
/// IntelliSense sees of them: rewritten by a real build when the list changed, and made empty by a
/// design-time build when it isn't there yet, so Visual Studio watches it from the start.
/// </summary>
public sealed class DamlWriteGeneratedProps : Task
{
    /// <summary>The props file.</summary>
    [Required]
    public string PropsPath { get; set; } = "";

    /// <summary>The generated files to compile, each with <c>EqDamlGenerationId</c>.</summary>
    public ITaskItem[] Files { get; set; } = [];

    /// <summary>Whether Solution Explorer shows them: in Source mode, not under obj.</summary>
    public bool Visible { get; set; }

    /// <summary>Only make an empty file where there is none, as a design-time build may.</summary>
    public bool OnlyIfMissing { get; set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        var path = Path.GetFullPath(PropsPath);
        try
        {
            if (OnlyIfMissing)
            {
                GeneratedProps.EnsureExists(path);
            }
            else if (GeneratedProps.Write(path, Files.Select(f => new GeneratedFile(f.GetMetadata("FullPath"), f.GetMetadata("EqDamlGenerationId"))), Visible))
            {
                Log.LogMessage(MessageImportance.Low, $"{path} lists the generated files now.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The build itself compiles the right files; only IntelliSense lags behind.
            Log.LogWarning($"{path} couldn't be written ({ex.Message}); Visual Studio may show generated types only after the next build.");
        }
        return true;
    }
}
