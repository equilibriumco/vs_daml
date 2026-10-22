using Equilibrium.Daml.Build.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Diagnostics;

public class SecretRedactionTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("proxyconnect tcp: http://ann:s3cret@proxy:8080", "proxyconnect tcp: http://***@proxy:8080")]
    [InlineData("Get \"https://token@ghcr.io/v2/x\": denied", "Get \"https://***@ghcr.io/v2/x\": denied")]
    [InlineData("from oci://ann:pw@mirror.example/a and https://bob:pw2@other/b", "from oci://***@mirror.example/a and https://***@other/b")]
    [InlineData("Head \"https://ghcr.io/v2/peacefulstudio/dpm-codegen-cs/manifests/sha256:9f04\"", "Head \"https://ghcr.io/v2/peacefulstudio/dpm-codegen-cs/manifests/sha256:9f04\"")]
    [InlineData("contact ann@example.com", "contact ann@example.com")]
    public void Credentials_in_urls_are_masked_and_nothing_else_is_touched(string text, string expected) =>
        SecretRedaction.Redact(text).ShouldBe(expected);
}
