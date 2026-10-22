using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Equilibrium.Daml.VisualStudio.Core.Options;
using Equilibrium.Daml.VisualStudio.Core.Server;

namespace Equilibrium.Daml.VisualStudio.Options;

/// <summary>
/// Tools &gt; Options &gt; Daml &gt; General, stored in the VS settings store. The page hands it out
/// as its automation object, for DTE.Properties("Daml", "General"); COM only sees it through
/// <see cref="IDamlGeneralOptions"/>, as a class with a generic base gets no class interface.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class DamlOptions : BaseOptionModel<DamlOptions>, IDamlGeneralOptions
{
    private const string Tools = "Tools";
    private const string Server = "Language server";
    private const string Bindings = "C# bindings";

    /// <inheritdoc />
    [Category(Tools)]
    [DisplayName("dpm path")]
    [Description("Full path to dpm.exe or dpm.cmd. Leave empty to look on PATH and in the default install folder.")]
    [DefaultValue("")]
    public string DpmPath { get; set; } = "";

    /// <inheritdoc />
    [Category(Tools)]
    [DisplayName("Daml assistant path")]
    [Description("Full path to the daml assistant, used for projects on SDKs older than 3.4. Leave empty to look on PATH and in the default install folder.")]
    [DefaultValue("")]
    public string DamlAssistantPath { get; set; } = "";

    /// <inheritdoc />
    [Category(Server)]
    [DisplayName("Use multi-package server")]
    [Description("Run one damlc multi-ide server for the whole folder. Turn off to run damlc ide for the package of the active file only.")]
    [DefaultValue(true)]
    public bool UseMultiIde { get; set; } = true;

    /// <inheritdoc />
    [Category(Server)]
    [DisplayName("Log level")]
    [Description("How much the language server writes to the Daml Language Server pane of the Output window.")]
    [DefaultValue(ServerLogLevel.Info)]
    public ServerLogLevel LogLevel { get; set; } = ServerLogLevel.Info;

    /// <inheritdoc />
    [Category(Server)]
    [DisplayName("Extra arguments")]
    [Description("Additional damlc arguments, separated by spaces. Use double quotes around an argument that contains spaces.")]
    [DefaultValue("")]
    public string ExtraArguments { get; set; } = "";

    /// <inheritdoc />
    [Category(Server)]
    [DisplayName("Run all scripts on open")]
    [Description("Run every script in a Daml file as soon as it opens, not only when you click its code lens.")]
    [DefaultValue(false)]
    public bool AutorunAllTests { get; set; }

    /// <inheritdoc />
    [Category(Server)]
    [DisplayName("Restart an unresponsive server")]
    [Description("Check every minute that the language server still answers, and restart it when it doesn't.")]
    [DefaultValue(true)]
    public bool KeepAliveEnabled { get; set; } = true;

    /// <inheritdoc />
    [Category(Bindings)]
    [DisplayName("Update C# bindings in the background (preview)")]
    [Description("After a Daml file is saved, build its Daml project and regenerate the C# of the projects that use it, without compiling them, so IntelliSense sees the change before the next build. Building the solution does the same whether this is on or not.")]
    [DefaultValue(false)]
    public bool LiveBindingSync { get; set; }

    /// <summary>The stored settings, loaded without blocking.</summary>
    public static async Task<DamlSettings> GetSettingsAsync() => (await GetLiveInstanceAsync()).ToSettings();

    /// <summary>A snapshot of these values for the rest of the extension.</summary>
    public DamlSettings ToSettings() => new()
    {
        DpmPath = DpmPath,
        DamlAssistantPath = DamlAssistantPath,
        UseMultiIde = UseMultiIde,
        LogLevel = LogLevel,
        ExtraArguments = ExtraArguments,
        AutorunAllTests = AutorunAllTests,
        KeepAliveEnabled = KeepAliveEnabled,
        LiveBindingSync = LiveBindingSync,
    };
}

/// <summary>The settings of Tools &gt; Options &gt; Daml &gt; General, as automation sees them.</summary>
[ComVisible(true)]
[Guid("749041ab-0d39-478c-bbd6-79587dd97234")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDamlGeneralOptions
{
    /// <summary>dpm to run; empty looks for it.</summary>
    string DpmPath { get; set; }

    /// <summary>The daml assistant to run for SDKs before dpm; empty looks for it.</summary>
    string DamlAssistantPath { get; set; }

    /// <summary>Whether one damlc multi-ide serves the whole folder.</summary>
    bool UseMultiIde { get; set; }

    /// <summary>How much the language server logs.</summary>
    ServerLogLevel LogLevel { get; set; }

    /// <summary>More damlc arguments, as typed.</summary>
    string ExtraArguments { get; set; }

    /// <summary>Whether every script of a file runs when it opens.</summary>
    bool AutorunAllTests { get; set; }

    /// <summary>Whether a server that stops answering is restarted.</summary>
    bool KeepAliveEnabled { get; set; }

    /// <summary>Whether saving a Daml file regenerates the C# of the projects that use it.</summary>
    bool LiveBindingSync { get; set; }
}

/// <summary>Holds the dialog pages; VS creates them through COM.</summary>
internal static class DamlOptionPages
{
    /// <summary>Tools &gt; Options &gt; Daml &gt; General.</summary>
    [ComVisible(true)]
    [Guid("5d3a6f0e-8b2c-4e71-9a64-2f1c7b0d9e53")]
    public sealed class General : BaseOptionPage<DamlOptions>
    {
    }
}
