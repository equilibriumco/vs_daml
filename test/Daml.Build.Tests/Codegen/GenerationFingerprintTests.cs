using System.Linq;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public class GenerationFingerprintTests
{
    private static readonly GenerationFingerprintInputs Inputs = new(
        GenerationId: "0123456789abcdef",
        DarSha256: "da12",
        GeneratorDigest: "9f04",
        DpmVersion: "1.0.22",
        SourceFingerprint: "50c");

    // Published fingerprints are compared with later builds' ones, so the recipe is fixed here.
    [Fact, Trait("Category", "Unit")]
    public void The_fingerprint_is_a_sha256_over_its_inputs_and_the_release()
    {
        var expected = Hashing.Sha256Text(
            "daml-generation-fingerprint/1\ncontract=2\nid=0123456789abcdef\ndar=da12\nsource=50c\ngenerator=9f04\nruntime=0.6.0\ndpm=1.0.22\n");

        GenerationFingerprint.Compute(Inputs).ShouldBe(expected);
    }

    // A DAR reference has no sources of its own; a project's come from its build.
    [Fact, Trait("Category", "Unit")]
    public void Without_a_source_fingerprint_its_line_is_empty()
    {
        var expected = Hashing.Sha256Text(
            "daml-generation-fingerprint/1\ncontract=2\nid=0123456789abcdef\ndar=da12\nsource=\ngenerator=9f04\nruntime=0.6.0\ndpm=1.0.22\n");

        GenerationFingerprint.Compute(Inputs with { SourceFingerprint = null }).ShouldBe(expected);
    }

    [Fact, Trait("Category", "Unit")]
    public void Every_input_changes_it()
    {
        var fingerprint = GenerationFingerprint.Compute(Inputs);

        new[]
        {
            Inputs with { GenerationId = "fedcba9876543210" },
            Inputs with { DarSha256 = "da13" },
            Inputs with { GeneratorDigest = "9f05" },
            Inputs with { DpmVersion = "1.0.23" },
            Inputs with { SourceFingerprint = "50d" },
        }.Select(GenerationFingerprint.Compute).ShouldAllBe(other => other != fingerprint);
    }
}
