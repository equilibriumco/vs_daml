using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Shouldly;
using StreamJsonRpc;
using Xunit;

namespace Daml.TestHelpers.Tests;

public class FakeLanguageServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact, Trait("Category", "Unit")]
    public async Task Initialize_returns_the_scripted_capabilities()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario
        {
            Capabilities = new JObject { ["hoverProvider"] = true, ["definitionProvider"] = true },
        });
        using var session = server.Start();

        var result = await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { processId = (int?)null, rootUri = (string?)null, capabilities = new { } });

        result["capabilities"]!["hoverProvider"]!.Value<bool>().ShouldBeTrue();
        result["capabilities"]!["definitionProvider"]!.Value<bool>().ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Clean_shutdown_exits_with_zero_and_records_the_conversation()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var session = server.Start();

        await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { capabilities = new { } });
        await session.Rpc.NotifyWithParameterObjectAsync("initialized", new { });
        await session.Rpc.InvokeAsync<object?>("shutdown");
        await session.Rpc.NotifyAsync("exit");

        (await session.WaitForExitAsync(Timeout)).ShouldBe(0);
        server.ReadReceivedMethods().ShouldBe(new[] { "initialize", "initialized", "shutdown", "exit" });
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Exit_without_shutdown_exits_with_one()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var session = server.Start();

        await session.Rpc.InvokeWithParameterObjectAsync<JObject>("initialize", new { capabilities = new { } });
        await session.Rpc.NotifyAsync("exit");

        (await session.WaitForExitAsync(Timeout)).ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Closing_stdin_ends_the_process()
    {
        using var server = FakeLanguageServer.Create(new FakeLanguageServerScenario());
        using var session = server.Start();

        session.CloseInput();

        (await session.WaitForExitAsync(Timeout)).ShouldBe(1);
    }
}
