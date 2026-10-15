using Daml.Build.Core.Configuration;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Configuration;

public class CodegenComponentPinTests
{
    private const string Digest = "9f040219936298f085e2ade77fbbc37829df065b879633282aa4628c9a1b6548";

    [Fact, Trait("Category", "Unit")]
    public void The_released_generator_is_pinned_by_digest()
    {
        var pin = CodegenComponentPin.TryParse(DamlProperties.CodegenComponent.Default);

        pin.ShouldNotBeNull();
        pin.Repository.ShouldBe("ghcr.io/peacefulstudio/dpm-codegen-cs");
        pin.Tag.ShouldBe("0.6.0");
        pin.Digest.ShouldBe(Digest);
        pin.Reference.ShouldBe(DamlProperties.CodegenComponent.Default);
    }

    [Fact, Trait("Category", "Unit")]
    public void A_registry_with_a_port_is_fine() =>
        CodegenComponentPin.TryParse($"oci://localhost:5000/mirror/dpm-codegen-cs:0.6.0@sha256:{Digest}")!
            .Repository.ShouldBe("localhost:5000/mirror/dpm-codegen-cs");

    [Theory, Trait("Category", "Unit")]
    [InlineData("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0")]
    [InlineData("oci://ghcr.io/peacefulstudio/dpm-codegen-cs@sha256:" + Digest)]
    [InlineData("ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:" + Digest)]
    [InlineData("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9f0402")]
    [InlineData("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:9F040219936298F085E2ADE77FBBC37829DF065B879633282AA4628C9A1B6548")]
    [InlineData("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha512:" + Digest)]
    [InlineData("oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0@sha256:" + Digest + " ")]
    [InlineData("")]
    public void Only_a_component_pinned_by_its_sha256_digest_is_accepted(string text) =>
        CodegenComponentPin.TryParse(text).ShouldBeNull();
}
