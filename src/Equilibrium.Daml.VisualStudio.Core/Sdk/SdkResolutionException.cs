using System;

namespace Equilibrium.Daml.VisualStudio.Core.Sdk;

/// <summary>
/// No usable Daml tool for the workspace. The message says what to install or configure and is
/// meant to be shown to the user as is.
/// </summary>
public sealed class SdkResolutionException : Exception
{
    /// <summary>Code for a missing dpm, daml assistant or SDK.</summary>
    public const string MissingToolCode = "DAML0101";

    /// <summary>Creates the exception with <see cref="MissingToolCode"/>.</summary>
    public SdkResolutionException(string message)
        : this(MissingToolCode, message)
    {
    }

    /// <summary>Creates the exception with a specific code.</summary>
    public SdkResolutionException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Diagnostic code, e.g. DAML0101.</summary>
    public string Code { get; }
}
