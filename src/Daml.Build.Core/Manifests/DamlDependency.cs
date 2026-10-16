namespace Daml.Build.Core.Manifests;

/// <summary>An entry of <c>dependencies</c> or <c>data-dependencies</c>.</summary>
/// <param name="Text">As the manifest writes it.</param>
/// <param name="DarPath">The full path when the entry is a DAR file; null for a package name such as <c>daml-stdlib</c>.</param>
public sealed record DamlDependency(string Text, string? DarPath);
