namespace Daml.Build.Core.Codegen;

/// <summary>
/// The Daml sources a project reference's DAR was built from, as that project's build reports
/// them. Navigation only maps generated C# to local sources that still match this fingerprint.
/// </summary>
/// <param name="ManifestPath">The project's <c>daml.yaml</c>.</param>
/// <param name="Fingerprint">The fingerprint of the sources the DAR was built from.</param>
public sealed record SourceProvenance(string ManifestPath, string Fingerprint);
