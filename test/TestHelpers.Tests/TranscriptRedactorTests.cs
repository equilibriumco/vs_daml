using Daml.TestHelpers;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;

namespace Daml.TestHelpers.Tests;

// Transcripts leave the machine (CI artifacts, fixtures in the repository), so every planted
// secret below must be gone after redaction.
public class TranscriptRedactorTests
{
    private const string Root = @"C:\Users\jdoe-private\AppData\Local\Temp\daml-contract\single-package-3.6.0-1234abcd";

    private static readonly TranscriptRedactor Redactor = new(new[]
    {
        (Root, "${ROOT}"),
        (@"C:\Users\jdoe-private\AppData\Local\Temp", "${TEMP}"),
        (@"C:\Users\jdoe-private", "${HOME}"),
    });

    private static string Redacted(string json) => Redactor.Redact(JObject.Parse(json)).ToString(Newtonsoft.Json.Formatting.None);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\\Users\\jdoe-private\\AppData\\Local\\Temp\\daml-contract\\single-package-3.6.0-1234abcd\\daml\\Main.daml", "${ROOT}\\\\daml\\\\Main.daml")]
    [InlineData("C:/Users/jdoe-private/AppData/Local/Temp/daml-contract/single-package-3.6.0-1234abcd/daml/Main.daml:5:9: error", "${ROOT}/daml/Main.daml:5:9: error")]
    [InlineData("file:///C:/Users/jdoe-private/AppData/Local/Temp/daml-contract/single-package-3.6.0-1234abcd/daml/Main.daml", "file:///${ROOT}/daml/Main.daml")]
    [InlineData("file:///c%3A/users/JDOE-PRIVATE/AppData/Local/Temp/daml-contract/single-package-3.6.0-1234abcd/daml/Main.daml", "file:///${ROOT}/daml/Main.daml")]
    [InlineData("file:///C:/Users/jdoe-private/AppData/Roaming/dpm/cache/daml-stdlib/DA/List.daml", "file:///${HOME}/AppData/Roaming/dpm/cache/daml-stdlib/DA/List.daml")]
    [InlineData(@"C:\\Users\\jdoe-private\\AppData\\Local\\Temp\\other", "${TEMP}\\\\other")]
    public void Paths_in_any_spelling_become_placeholders(string original, string expected)
    {
        Redacted($$$"""{"params":{"uri":"{{{original}}}"}}""").ShouldBe($$$"""{"params":{"uri":"{{{expected}}}"}}""");
    }

    [Fact, Trait("Category", "Unit")]
    public void Document_text_and_virtual_resource_contents_are_replaced_by_their_length()
    {
        var opened = Redacted("""{"method":"textDocument/didOpen","params":{"textDocument":{"uri":"x","text":"secret code"}}}""");
        var changed = Redacted("""{"method":"textDocument/didChange","params":{"contentChanges":[{"text":"more code"}]}}""");
        var resource = Redacted("""{"method":"daml/virtualResource/didChange","params":{"uri":"daml://x","contents":"<table>party data</table>"}}""");

        opened.ShouldNotContain("secret code");
        opened.ShouldContain("<redacted 11 chars>");
        changed.ShouldNotContain("more code");
        resource.ShouldNotContain("party data");
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ012345 in a log line", "ghp_0123456789")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("DPM_REGISTRY_PASSWORD=hunter2-very-secret", "hunter2")]
    [InlineData("api_key: sk-live-abcdef0123456789", "sk-live")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE key", "AKIAIOSFODNN7EXAMPLE")]
    public void Secrets_in_any_text_are_masked(string text, string leak)
    {
        var redacted = Redacted($$$"""{"method":"window/logMessage","params":{"message":"{{{text}}}"}}""");

        redacted.ShouldNotContain(leak);
        redacted.ShouldContain("<redacted secret>");
    }

    [Fact, Trait("Category", "Unit")]
    public void Ordinary_messages_stay_as_they_are()
    {
        const string Json = """{"jsonrpc":"2.0","id":3,"result":{"contents":{"kind":"markdown","value":"template Asset"},"range":{"start":{"line":6,"character":9}}}}""";

        Redacted(Json).ShouldBe(Json);
    }

    [Fact, Trait("Category", "Unit")]
    public void Property_names_are_redacted_too()
    {
        // workspace/didChangeWatchedFiles and diagnostics maps can key by path.
        var redacted = Redacted("""{"result":{"C:/Users/jdoe-private/AppData/Local/Temp/daml-contract/single-package-3.6.0-1234abcd/daml.yaml":1}}""");

        redacted.ShouldNotContain("jdoe-private");
        redacted.ShouldContain("${ROOT}/daml.yaml");
    }
}
