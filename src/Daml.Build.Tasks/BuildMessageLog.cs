using Daml.Build.Core.Building;
using Daml.Build.Core.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Daml.Build.Tasks;

internal static class BuildMessageLog
{
    // The text goes in as the message itself, never as a format: it quotes tool output, braces and all.
    public static void Report(this TaskLoggingHelper log, BuildMessage message)
    {
        switch (message.Severity)
        {
            case DamlDiagnosticSeverity.Error:
                log.LogError(message.Subcategory, message.Code, null, message.File, message.Line, message.Column, message.EndLine, message.EndColumn, message.Text);
                break;
            case DamlDiagnosticSeverity.Warning:
                log.LogWarning(message.Subcategory, message.Code, null, message.File, message.Line, message.Column, message.EndLine, message.EndColumn, message.Text);
                break;
            default:
                log.LogMessage(message.Subcategory, message.Code, null, message.File, message.Line, message.Column, message.EndLine, message.EndColumn, MessageImportance.Normal, message.Text);
                break;
        }
    }
}
