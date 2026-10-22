using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Newtonsoft.Json.Linq;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Contract;

// Visual Studio closes a document whose edits were thrown away and opens it again from disk.
// The adapter keeps that close from damlc and sends the reopened text as a change; damlc must
// end up with exactly the reopened text, or its answers (hover ranges, diagnostics) point into
// text Visual Studio doesn't have.
public class DamlcReopenTextTests
{
    private readonly ITestOutputHelper _output;

    public DamlcReopenTextTests(ITestOutputHelper output) => _output = output;

    [SkippableTheory, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    [InlineData("multi-ide", 1)]
    [InlineData("multi-ide", 10)]
    [InlineData("ide", 1)]
    [InlineData("ide", 10)]
    public async Task After_a_reopen_damlc_answers_for_the_reopened_text(string mode, int reopenVersion)
    {
        using var sample = SampleCopy.Create("single-package", "3.6.0");
        var main = sample.File(@"daml\Main.daml");
        var disk = File.ReadAllText(main);
        var edited = string.Concat(Enumerable.Repeat("\n", 20)) + disk;
        var uri = RealDamlcSession.Uri(main);
        await using var damlc = await RealDamlcSession.StartAsync(sample, main, mode == "ide" ? ServerMode.Ide : ServerMode.MultiIde);
        var rpc = damlc.Rpc;

        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "daml", version = 1, text = disk } });
        await rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = edited } } });
        await rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } });
        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "daml", version = reopenVersion, text = disk } });

        // "template Asset" on disk; with the edit still in damlc, it would be 20 lines further down.
        var lines = disk.Replace("\r\n", "\n").Split('\n');
        var line = Array.FindIndex(lines, l => l.StartsWith("template Asset", StringComparison.Ordinal));
        var character = lines[line].IndexOf("Asset", StringComparison.Ordinal);
        JToken? hover = null;
        var deadline = DateTime.UtcNow + RealDamlcSession.Patience;
        while (hover is not JObject && DateTime.UtcNow < deadline)
        {
            hover = await damlc.RequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line, character } });
            if (hover is not JObject)
            {
                await Task.Delay(500);
            }
        }

        _output.WriteLine(hover?.ToString() ?? "no hover");
        hover.ShouldBeOfType<JObject>("no hover for the reopened text; damlc may still be on the edited one");
        if (hover!["range"] is JObject range)
        {
            ((int)range["start"]!["line"]!).ShouldBe(line, "the hover range belongs to the text damlc had before the reopen");
        }
        ((string?)hover["contents"]?.ToString()).ShouldNotBeNull().ShouldContain("Asset");
    }
}
