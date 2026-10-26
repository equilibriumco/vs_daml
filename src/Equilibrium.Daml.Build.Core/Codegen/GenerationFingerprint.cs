using System;
using System.Globalization;
using Equilibrium.Daml.Build.Core.FileSystem;

namespace Equilibrium.Daml.Build.Core.Codegen;

/// <summary>
/// One value for everything a generation's code depends on: when it differs from the one in
/// the published manifest, the generation is regenerated, whatever the files' timestamps say.
/// The build's configuration and target framework are left out on purpose, since the code
/// doesn't depend on them.
/// </summary>
internal static class GenerationFingerprint
{
    /// <summary>The fingerprint of generating <paramref name="input"/> from a DAR with the SHA-256 <paramref name="darSha256"/>.</summary>
    public static string Of(CodegenInput input, string darSha256, string generatorDigest, string dpmVersion)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        return Compute(new GenerationFingerprintInputs(input.Reference.GenerationId, darSha256, generatorDigest, dpmVersion, input.Source?.Fingerprint));
    }

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
