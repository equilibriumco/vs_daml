using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Daml.VisualStudio.Core.Options;
using Daml.VisualStudio.Core.Server;

namespace Daml.VisualStudio.Options;

/// <summary>Tools &gt; Options &gt; Daml &gt; General, stored in the VS settings store.</summary>
internal sealed class DamlOptions : BaseOptionModel<DamlOptions>
{
    private const string Tools = "Tools";
    private const string Server = "Language server";

    [Category(Tools)]
    [DisplayName("dpm path")]
    [Description("Full path to dpm.exe or dpm.cmd. Leave empty to look on PATH and in the default install folder.")]
    [DefaultValue("")]
    public string DpmPath { get; set; } = "";

    [Category(Tools)]
    [DisplayName("Daml assistant path")]
    [Description("Full path to the daml assistant, used for projects on SDKs older than 3.4. Leave empty to look on PATH and in the default install folder.")]
    [DefaultValue("")]
    public string DamlAssistantPath { get; set; } = "";

    [Category(Server)]
    [DisplayName("Use multi-package server")]
    [Description("Run one damlc multi-ide server for the whole folder. Turn off to run damlc ide for the package of the active file only.")]
    [DefaultValue(true)]
    public bool UseMultiIde { get; set; } = true;

    [Category(Server)]
    [DisplayName("Log level")]
    [Description("How much the language server writes to the Daml Language Server pane of the Output window.")]
    [DefaultValue(ServerLogLevel.Info)]
    public ServerLogLevel LogLevel { get; set; } = ServerLogLevel.Info;

    [Category(Server)]
    [DisplayName("Extra arguments")]
    [Description("Additional damlc arguments, separated by spaces. Use double quotes around an argument that contains spaces.")]
    [DefaultValue("")]
    public string ExtraArguments { get; set; } = "";

    [Category(Server)]
    [DisplayName("Run all scripts on open")]
    [Description("Run every script in a Daml file as soon as it opens, not only when you click its code lens.")]
    [DefaultValue(false)]
    public bool AutorunAllTests { get; set; }

    [Category(Server)]
    [DisplayName("Restart an unresponsive server")]
    [Description("Check every minute that the language server still answers, and restart it when it doesn't.")]
    [DefaultValue(true)]
    public bool KeepAliveEnabled { get; set; } = true;

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
    };
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
