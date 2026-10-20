using System;
using System.Globalization;
using Daml.Build.Core.FileSystem;

namespace Daml.Build.Core.Codegen;

/// <summary>
/// One value for everything a generation's code depends on: when it differs from the one in
/// the published manifest, the generation is regenerated, whatever the files' timestamps say.
/// The build's configuration and target framework are left out on purpose, since the code
/// doesn't depend on them.
/// </summary>
public static class GenerationFingerprint
{
    /// <summary>The SHA-256 over <paramref name="inputs"/>, the generation contract and the runtime version.</summary>
    public static string Compute(GenerationFingerprintInputs inputs)
    {
        if (inputs is null)
        {
            throw new ArgumentNullException(nameof(inputs));
        }
        return Hashing.Sha256Text(string.Join(
            "\n",
            "daml-generation-fingerprint/1",
            "contract=" + ReleaseTools.GenerationContract.ToString(CultureInfo.InvariantCulture),
            "id=" + inputs.GenerationId,
            "dar=" + inputs.DarSha256,
            "source=" + inputs.SourceFingerprint,
            "generator=" + inputs.GeneratorDigest,
            "runtime=" + ReleaseTools.Runtime.Version,
            "dpm=" + inputs.DpmVersion,
            ""));
    }
}
