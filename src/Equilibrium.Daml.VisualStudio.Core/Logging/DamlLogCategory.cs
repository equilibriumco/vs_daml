namespace Equilibrium.Daml.VisualStudio.Core.Logging;

/// <summary>Area of the extension a log entry comes from. Each category gets its own output channel.</summary>
public enum DamlLogCategory
{
    /// <summary>Package lifetime, editor features and anything not covered below.</summary>
    Extension,

    /// <summary>The damlc language server process and its protocol traffic.</summary>
    LanguageServer,

    /// <summary>dpm build/test runs and their output.</summary>
    Build,
}
