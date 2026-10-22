using Equilibrium.Daml.Build.Core.Manifests;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Project;
using Equilibrium.Daml.VisualStudio.Core.Sdk;
using Equilibrium.Daml.VisualStudio.Core.Server;
using Equilibrium.Daml.VisualStudio.LanguageServer.Hosting;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Hosting;

// damlc multi-ide looks for a daml.yaml above every document it's sent and, when there is none,
// takes the whole process down. Documents outside the workspace must never reach it.
public sealed class DocumentScopeTests : System.IDisposable
{
    private readonly LanguageServerHost _host;

    public DocumentScopeTests()
    {
        var files = new InMemoryFileSystem();
        var runner = new FakeProcessRunner();
        _host = new LanguageServerHost(runner, new SdkResolver(runner, new AssistantLocator(files, _ => null), new YamlManifestReader(files)), NullDamlLogger.Instance);
    }

    public void Dispose() => _host.Dispose();

    private void Workspace() => _host.SetWorkspace(new WorkspaceContext
    {
        WorkspaceRoot = @"M:\repo\samples\broken",
        ServerWorkingDirectory = @"M:\repo\samples\broken",
        Mode = ServerMode.MultiIde,
        PackageRoot = @"M:\repo\samples\broken",
        Packages = [@"M:\repo\samples\broken"],
        Version = 1,
    }, new LanguageServerOptions());

    [Theory, Trait("Category", "Unit")]
    [InlineData("file:///M:/repo/samples/broken/daml/TypeError.daml", true)]
    [InlineData("file:///m%3A/repo/samples/broken/daml/TypeError.daml", true)]
    [InlineData("file:///m:/REPO/Samples/Broken/daml/TypeError.daml", true)]
    [InlineData("file:///M:/repo/test/fixtures/lexer/keywords.daml", false)]
    [InlineData("file:///M:/repo/samples/brokenish/Other.daml", false)]
    [InlineData("daml://compiler?file=x&template=T", true)]
    [InlineData("untitled:Untitled-1", true)]
    [InlineData(null, true)]
    [InlineData("not a uri", true)]
    public void Only_files_inside_the_workspace_are_sent_to_the_server(string? uri, bool served)
    {
        Workspace();

        _host.IsServed(uri).ShouldBe(served);
    }

    // Go To Definition into a dependency opens damlc's own copy under .daml\package-database; sent back to
    // the server it would not compile there, and the Error List would fill with "Could not find module Pkg_…".
    [Theory, Trait("Category", "Unit")]
    [InlineData("file:///M:/repo/samples/broken/.daml/package-database/2.3/contracts-0.1.0-db3f/Asset.daml")]
    [InlineData("file:///M:/repo/samples/broken/.DAML/dist/Main.daml")]
    public void Copies_under_the_build_output_are_not_sent_to_the_server(string uri)
    {
        Workspace();

        _host.IsServed(uri).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Without_a_workspace_no_file_is_served()
    {
        _host.SetWorkspace(null, new LanguageServerOptions());

        _host.IsServed("file:///M:/repo/samples/broken/daml/TypeError.daml").ShouldBeFalse();
        _host.IsServed("daml://compiler?file=x").ShouldBeTrue();
    }
}
