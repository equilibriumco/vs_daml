using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Locking;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Locking;

public sealed class FileLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-locks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Lock => Path.Combine(_root, "nested", "codegen.lock");

    [Fact, Trait("Category", "Unit")]
    public async Task A_free_lock_is_taken_at_once_and_its_folder_made()
    {
        using var held = await FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);

        File.Exists(Lock).ShouldBeTrue();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_second_holder_waits_until_the_first_lets_go()
    {
        var first = await FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(300);
        second.IsCompleted.ShouldBeFalse();

        first.Dispose();

        using var taken = await second;
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Waiting_past_the_limit_is_a_timeout()
    {
        using var held = await FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);
        var clock = Stopwatch.StartNew();

        await Should.ThrowAsync<TimeoutException>(() => FileLock.AcquireAsync(Lock, TimeSpan.FromMilliseconds(400), CancellationToken.None));

        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Cancelling_stops_the_wait()
    {
        using var held = await FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(30), cancellation.Token));
    }

    // A build killed while it held the lock must not keep every later build waiting.
    [Fact, Trait("Category", "Process")]
    public async Task A_holder_that_dies_lets_go()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Lock)!);
        var script = $"$f = [IO.File]::Open('{Lock}', 'OpenOrCreate', 'ReadWrite', 'None'); 'held'; Start-Sleep -Seconds 60";
        using var holder = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        (await holder.StandardOutput.ReadLineAsync()).ShouldBe("held");
        await Should.ThrowAsync<TimeoutException>(() => FileLock.AcquireAsync(Lock, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        holder.Kill();
        holder.WaitForExit();

        using var taken = await FileLock.AcquireAsync(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    // Per user, so builds of different users never wait on each other; per canonical folder,
    // in any spelling, so two builds writing one folder always do.
    [Fact, Trait("Category", "Unit")]
    public void Locks_are_named_after_what_they_guard()
    {
        var root = LockNames.Root(name => name == "LOCALAPPDATA" ? @"C:\Users\ann\AppData\Local" : null);

        root.ShouldBe(@"C:\Users\ann\AppData\Local\EquilibriumDamlTools\locks");
        LockNames.Generator(root, "9f04").ShouldBe(root + @"\codegen-9f04.lock");
        LockNames.Output(root, @"C:\work\App\Generated\Daml").ShouldBe(LockNames.Output(root, @"c:\WORK\app\generated\daml\"));
        LockNames.Output(root, @"C:\work\App\Generated\Daml").ShouldNotBe(LockNames.Output(root, @"C:\work\App2\Generated\Daml"));
        Path.GetFileName(LockNames.Output(root, @"C:\work\App\Generated\Daml")).ShouldMatch("^output-[0-9a-f]{16}[.]lock$");
        LockNames.Package(root, @"C:\work\Contracts").ShouldBe(LockNames.Package(root, @"c:\work\contracts\"));
        Path.GetFileName(LockNames.Package(root, @"C:\work\Contracts")).ShouldMatch("^package-[0-9a-f]{16}[.]lock$");
    }

    // A tool task waits on its own thread.
    [Fact, Trait("Category", "Unit")]
    public void The_waiting_kind_takes_a_lock_once_it_is_free_and_gives_up_like_the_other()
    {
        var held = FileLock.Acquire(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);

        Should.Throw<TimeoutException>(() => FileLock.Acquire(Lock, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            Should.Throw<OperationCanceledException>(() => FileLock.Acquire(Lock, TimeSpan.FromSeconds(30), cancellation.Token));
        }
        held.Dispose();

        using var taken = FileLock.Acquire(Lock, TimeSpan.FromSeconds(5), CancellationToken.None);
    }
}
