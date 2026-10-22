using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;
using Shouldly;
using Xunit;
using static Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol.LspFramingTests;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol;

#pragma warning disable CA1835
public class BoundedTransportTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(2, 4096)]
    [InlineData(256, 100)]
    public async Task A_stalled_consumer_cannot_accumulate_unlimited_server_messages(int frames, int bytes)
    {
        var frame = Frame("{\"method\":\"window/logMessage\",\"params\":{\"message\":\"payload\"}}");
        using var input = new MemoryStream(Enumerable.Range(0, 300).SelectMany(_ => frame).ToArray());
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new LspAdapterSettings { MaxQueuedFrames = frames, MaxQueuedBytes = bytes };
        using var stream = new ServerToClientStream(input, settings, new InitializeTracker(), NullDamlLogger.Instance,
            onFailure: error => failed.TrySetResult(error));
        var oneByte = new byte[1];
        // Start the pump, then stop consuming. It must fail rather than grow behind the client.
        try { (await stream.ReadAsync(oneByte, 0, 1, CancellationToken.None)).ShouldBe(1); }
        catch (IOException) { }
        (await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(failed.Task);
        (await failed.Task).ShouldBeOfType<IOException>();
        await Should.ThrowAsync<IOException>(() => stream.CopyToAsync(Stream.Null));
    }
}
