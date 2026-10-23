using System;
using System.Collections.Generic;
using System.Text;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.Sdk;

namespace Equilibrium.Daml.VisualStudio.Core.Server;

/// <summary>
/// Turns the resolved tool, the workspace and the user's options into the command that starts
/// damlc as a language server: <c>&lt;dpm|daml&gt; damlc multi-ide|ide [options]</c>.
/// </summary>
public static class LanguageServerCommandBuilder
{
    /// <summary>Builds the server command.</summary>
    /// <exception cref="InvalidOperationException">
    /// The workspace is set up for multi-ide but the resolved damlc is known not to have it.
    /// </exception>
    public static ProcessSpec Build(SdkResolution sdk, WorkspaceContext workspace, LanguageServerOptions options)
    {
        if (sdk is null)
        {
            throw new ArgumentNullException(nameof(sdk));
        }
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var multiIde = workspace.Mode == ServerMode.MultiIde;
        if (multiIde && sdk.SupportsMultiIde == false)
        {
            throw new InvalidOperationException(
                $"damlc {sdk.CoordinatorVersion ?? sdk.ContextSdkVersion} has no multi-ide server; the workspace has to be resolved in ide mode.");
        }

        var arguments = new List<string> { "damlc", multiIde ? "multi-ide" : "ide" };
        if (multiIde)
        {
            arguments.Add("--log-level=" + LogLevelName(options.LogLevel));
        }
        else if (options.LogLevel == ServerLogLevel.Debug)
        {
            arguments.Add("--debug");
        }
        if (options.AutorunAllTests)
        {
            arguments.Add("--studio-auto-run-all-scripts=yes");
        }
        arguments.AddRange(SplitArguments(options.ExtraArguments));

        // No telemetry flags: damlc 3.5/3.6 lists none, and we pass none of the hidden ones. The environment is inherited untouched;
        // in particular DPM_AUTO_INSTALL is left to the user, since SDK installs are reported
        // through the server's own progress messages.
        return new ProcessSpec(sdk.Assistant.Path, arguments) { WorkingDirectory = workspace.ServerWorkingDirectory };
    }

    /// <summary>
    /// Splits a user-typed argument string on whitespace. Double quotes group text containing
    /// whitespace and are removed; there are no escapes, so an argument can't contain a quote.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        var arguments = new List<string>();
        if (text is null)
        {
            return arguments;
        }

        var current = new StringBuilder();
        var inArgument = false;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                inArgument = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (inArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }
            }
            else
            {
                current.Append(c);
                inArgument = true;
            }
        }
        if (inArgument)
        {
            arguments.Add(current.ToString());
        }
        return arguments;
    }

    private static string LogLevelName(ServerLogLevel level) => level switch
    {
        ServerLogLevel.Debug => "DEBUG",
        ServerLogLevel.Warning => "WARNING",
        ServerLogLevel.Error => "ERROR",
        _ => "INFO",
    };
}
