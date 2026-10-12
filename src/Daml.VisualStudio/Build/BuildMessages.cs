using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Daml.Build.Core.Diagnostics;
using Daml.VisualStudio.Core.Build;
using Microsoft.VisualStudio.Workspace.Build;

namespace Daml.VisualStudio.Build;

// Turns dpm's diagnostics into Open Folder build messages. Visual Studio then lists them in the
// Error List as build errors, navigates to them itself and clears them at the next build.
internal static class BuildMessages
{
    public const string ErrorCode = "DAML0001";
    public const string WarningCode = "DAML0002";

    public static IReadOnlyCollection<BuildMessage> From(IEnumerable<BuildDiagnostic> diagnostics, string projectFile) =>
        diagnostics.Select(d => From(d, projectFile)).ToList();

    public static BuildMessage From(BuildDiagnostic diagnostic, string projectFile)
    {
        var error = diagnostic.Severity == DamlDiagnosticSeverity.Error;
        var code = error ? ErrorCode : WarningCode;
        // Build messages count lines and columns from 1, as the compiler printed them.
        var line = diagnostic.Line + 1;
        var column = diagnostic.Column + 1;
        return new BuildMessage
        {
            Type = error ? BuildMessage.TaskType.Error : BuildMessage.TaskType.Warning,
            Code = code,
            TaskText = diagnostic.Message,
            File = diagnostic.FilePath,
            LineNumber = line,
            ColumnNumber = column,
            ProjectFile = projectFile,
            // Not used, but not left null either: VS hands these strings to native code too.
            SubCategory = "",
            HelpKeyword = "",
            // Visual Studio writes this into its Build pane, and a null here crashes the IDE.
            LogMessage = string.Format(CultureInfo.InvariantCulture, "{0}({1},{2}): {3} {4}: {5}\n",
                diagnostic.FilePath, line, column, error ? "error" : "warning", code, diagnostic.Message),
        };
    }
}
