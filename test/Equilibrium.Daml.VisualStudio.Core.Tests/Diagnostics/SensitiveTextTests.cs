using Equilibrium.Daml.VisualStudio.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Diagnostics;

// Each rule on its own, with the exact result: the diagnostic redactor runs other rules after these,
// which would hide a rule that stopped working.
public class SensitiveTextTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("dpm couldn't reach https://jdoe:hunter2pass@registry.example.com/v2/", "dpm couldn't reach https://<redacted secret>@registry.example.com/v2/")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl", "Authorization: <redacted secret>")]
    [InlineData("id_token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl here", "id_token <redacted secret> here")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ012345 in the environment", "token <redacted secret> in the environment")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE key", "aws <redacted secret> key")]
    [InlineData("DPM_REGISTRY_PASSWORD=hunter2-very-secret and more", "<redacted secret> and more")]
    [InlineData("api-key: abc123", "<redacted secret>")]
    public void Each_kind_of_secret_is_masked_and_nothing_else(string text, string expected) =>
        SensitiveText.MaskSecrets(text).ShouldBe(expected);

    [Theory, Trait("Category", "Unit")]
    [InlineData("https://registry.example.com/v2/ answered 404")]
    [InlineData("the password prompt was skipped")]
    [InlineData("AKIA is not a key on its own")]
    public void Text_without_secrets_stays_as_it_is(string text) =>
        SensitiveText.MaskSecrets(text).ShouldBe(text);
}
