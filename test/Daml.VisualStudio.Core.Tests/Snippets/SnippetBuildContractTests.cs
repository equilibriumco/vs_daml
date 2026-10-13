using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Processes;
using Daml.VisualStudio.Core.Snippets;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Daml.VisualStudio.Core.Tests.Snippets;

// Every snippet with its default values, dropped into a small module where it belongs, built by
// the real dpm: what completion inserts has to compile before anyone changes a single field.
public sealed class SnippetBuildContractTests : IDisposable
{
    private const string Header = "module {name} where\n\nimport Daml.Script\n\n";

    // Where each snippet goes: the module around it, with {snippet} where it's inserted.
    private static readonly Dictionary<string, string> Hosts = new()
    {
        ["template"] = "{snippet}",
        ["choice"] = Template("{snippet}"),
        ["nonconsuming choice"] = Template("{snippet}"),
        ["interface"] = "data MyView = MyView with\n    owner : Party\n  deriving (Eq, Show)\n\n{snippet}",
        ["interface instance"] = "data MyView = MyView with\n    owner : Party\n  deriving (Eq, Show)\n\ninterface MyInterface where\n  viewtype MyView\n\n" + Template("{snippet}", name: "MyTemplate"),
        ["script"] = "{snippet}",
    };

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-snippets-" + Path.GetRandomFileName());

    public SnippetBuildContractTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Template(string body, string name = "Host") =>
        $"template {name}\n  with\n    owner : Party\n  where\n    signatory owner\n\n    {body}";

    [SkippableFact, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    public async Task Every_snippet_builds_with_its_defaults()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        Directory.CreateDirectory(Path.Combine(_root, "daml"));
        // Daml 3 wants interfaces in a package of their own and refuses an instance next to its
        // interface; that's about package layout, not the snippet, so this one package allows it.
        File.WriteAllText(Path.Combine(_root, "daml.yaml"),
            "sdk-version: 3.6.0\nname: snippets\nsource: daml\nversion: 0.0.1\ndependencies:\n  - daml-prim\n  - daml-stdlib\n  - daml-script\nbuild-options:\n  - -Wno-upgrade-interfaces\n");

        foreach (var snippet in DamlSnippets.All)
        {
            var (module, text) = Module(snippet);
            _output.WriteLine($"--- {module}.daml");
            _output.WriteLine(text);
            File.WriteAllText(Path.Combine(_root, "daml", module + ".daml"), text);
        }

        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec(dpm, ["build"]) { WorkingDirectory = _root, Timeout = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        _output.WriteLine(result.Stdout);
        _output.WriteLine(result.Stderr);
        result.ExitCode.ShouldBe(0, "a snippet with its defaults doesn't build; see the output");
    }

    // The module snippet is a whole file; the others go into a host, indented like their line.
    private static (string Module, string Text) Module(DamlSnippet snippet)
    {
        if (snippet.Shortcut == "module")
        {
            var header = SnippetExpander.Expand(snippet.Body, "", "\n").Text;
            return (header.Split(' ')[1], header);
        }
        Hosts.ContainsKey(snippet.Shortcut).ShouldBeTrue($"no host module for the {snippet.Shortcut} snippet");
        var name = "Snippet" + string.Concat(snippet.Shortcut.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        var host = Hosts[snippet.Shortcut];
        var line = host.Split('\n').Single(l => l.Contains("{snippet}"));
        var indent = line.Substring(0, line.IndexOf("{snippet}", StringComparison.Ordinal));
        var text = Header.Replace("{name}", name) + host.Replace("{snippet}", SnippetExpander.Expand(snippet.Body, indent, "\n").Text);
        return (name, text);
    }
}
