using System;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Build;
using Daml.VisualStudio.Core.Logging;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Build;

public class BuildCoordinatorLifecycleTests
{
    // The paused runs are owned by this test and do not need a VS synchronization context.
#pragma warning disable VSTHRD003
    [Fact, Trait("Category", "Unit")]
    public async Task Disposal_cancels_running_and_queued_builds_without_breaking_their_cleanup()
    {
        var runner = new FakeProcessRunner();
        var coordinator = new DamlBuildCoordinator(new DpmRunner(runner), () => @"C:\dpm.exe", NullDamlLogger.Instance);
        var action = new DamlBuildAction(DpmCommandKind.Build, @"C:\repo", false);
        var running = coordinator.RunAsync(action, CancellationToken.None);
        var queued = coordinator.RunAsync(action, CancellationToken.None);
        runner.Started.Count.ShouldBe(1);

        coordinator.Dispose();
        coordinator.Dispose();

        await Should.ThrowAsync<OperationCanceledException>(() => running);
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        runner.Started[0].Disposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => coordinator.RunAsync(action, CancellationToken.None));
    }
#pragma warning restore VSTHRD003
}
