namespace Daml.Build.Core.Configuration;

/// <summary>A setting the build can't use. The build stops on it before any tool runs.</summary>
/// <param name="Code">The diagnostic code, one of <see cref="Diagnostics.DamlDiagnosticCodes"/>.</param>
/// <param name="Message">What is wrong and how to put it right.</param>
public sealed record ConfigurationError(string Code, string Message);
