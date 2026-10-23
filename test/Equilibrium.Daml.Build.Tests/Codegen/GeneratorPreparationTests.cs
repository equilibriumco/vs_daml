using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core.Codegen;
using Equilibrium.Daml.Build.Core.Configuration;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Locking;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

public sealed class GeneratorPreparationTests : IDisposable
{
    private const string Dpm = @"C:\dpm\bin\dpm.cmd";
    private static readonly string[] Install = ["install", "package"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-generator-setup-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticGeneratorCache _download;
    private readonly GeneratorCache _cache;
    private readonly FakeProcessRunner _runner = new();
    private readonly List<string> _installSaw = [];

    public GeneratorPreparationTests()
    {
        // What a download of the pin brings; the cache under test starts empty.
        _download = SyntheticGeneratorCache.Create(Path.Combine(_root, "download"));
        _cache = new GeneratorCache(Path.Combine(_root, "codegen"), _download.Pin);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Locks => Path.Combine(_root, "locks");

    private static ToolOptions Options(bool install = true, bool offline = false, double lockSeconds = 30) =>
        new(null, offline, install, SkipPreflight: false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(lockSeconds));

    private Task<ConfigurationError?> Prepare(ToolOptions options) =>
        new GeneratorPreparation(_runner, Locks).PrepareAsync(_cache, Dpm, options, CancellationToken.None);

    // dpm downloading the pin into the home it was started with.
    private void InstallSucceeds(Action<ProcessSpec>? before = null, int delayMilliseconds = 0) =>
        _runner.On(Dpm, Install, async spec =>
        {
            before?.Invoke(spec);
            await Task.Delay(delayMilliseconds);
            _download.CopyHomeTo(spec.Environment["DPM_HOME"]!);
            return new ProcessResult(0, "Installing components...\n", "");
        });

    private void Installed()
    {
        _download.CopyHomeTo(_cache.Home);
        _cache.MarkReady();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_cache_found_complete_before_needs_no_lock_and_no_process()
    {
        Installed();

        (await Prepare(Options())).ShouldBeNull();

        _runner.Calls.ShouldBeEmpty();
        Directory.Exists(Locks).ShouldBeFalse();
    }

    // Copied in by hand, or left by a build that died before it could mark it.
    [Fact, Trait("Category", "Unit")]
    public async Task A_complete_cache_without_the_marker_is_hashed_once_and_marked()
    {
        _download.CopyHomeTo(_cache.Home);

        (await Prepare(Options(install: false))).ShouldBeNull();

        _cache.IsMarkedReady.ShouldBeTrue();
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_missing_generator_is_installed_into_its_private_home()
    {
        InstallSucceeds(spec =>
        {
            File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "daml.yaml")).ShouldContain("  - " + _download.Pin.Reference);
            // dpm install package beside a multi-package.yaml installs for the packages it lists, and
            // an empty one has none: dpm 1.0.22 then succeeds without downloading anything.
            File.Exists(Path.Combine(spec.WorkingDirectory!, "multi-package.yaml")).ShouldBeFalse();
            _installSaw.Add(spec.WorkingDirectory!);
        });

        (await Prepare(Options())).ShouldBeNull();

        var call = _runner.Calls.ShouldHaveSingleItem();
        call.Environment["DPM_HOME"].ShouldBe(_cache.Home);
        call.Environment["DPM_AUTO_INSTALL"].ShouldBe("true");
        call.Environment["TEMP"].ShouldStartWith(call.WorkingDirectory!);
        call.Environment["TMP"].ShouldBe(call.Environment["TEMP"]);
        _cache.IsMarkedReady.ShouldBeTrue();
        Directory.Exists(_installSaw.Single()).ShouldBeFalse("the install folder stays behind");
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_leave_to_install_a_missing_generator_is_EQD0101_with_the_way_out()
    {
        var problem = (await Prepare(Options(install: false))).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.MissingTool);
        problem.Message.ShouldBe(
            "The C# code generator ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0 isn't in its cache: Nothing of the pinned component was downloaded. " +
            "Set EqDamlAllowToolInstall to true to let the build download it, once per generator version.");
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Offline_a_missing_generator_is_EQD0107()
    {
        var problem = (await Prepare(Options(install: false, offline: true))).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.MissingToolOffline);
        problem.Message.ShouldEndWith("EqDamlOffline is true, so the build doesn't download it. Build once online with EqDamlAllowToolInstall set to true.");
        _runner.Calls.ShouldBeEmpty();
    }

    // dpm doesn't check what it unpacked; the cut-short files have to go for it to unpack them again.
    [Fact, Trait("Category", "Unit")]
    public async Task A_cut_short_unpack_is_cleared_before_installing_again_and_the_downloads_stay()
    {
        Installed();
        File.WriteAllText(_download.ComponentFile("bin/daml-dar-to-proto.jar").Replace(_download.Home, _cache.Home), "PK");
        InstallSucceeds(_ =>
        {
            Directory.Exists(_cache.Component).ShouldBeFalse();
            Directory.EnumerateFiles(Path.Combine(_cache.Home, @"cache\oci-layout\blobs\sha256")).ShouldNotBeEmpty();
        });

        (await Prepare(Options())).ShouldBeNull();

        _cache.Check(hashFiles: true).Status.ShouldBe(GeneratorCacheStatus.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_damaged_download_starts_over_from_an_empty_home()
    {
        Installed();
        File.AppendAllText(Path.Combine(_cache.Home, @"cache\oci-layout\blobs\sha256", _download.Pin.Digest), " ");
        InstallSucceeds(_ => Directory.Exists(_cache.Home).ShouldBeFalse());

        (await Prepare(Options())).ShouldBeNull();

        _cache.Check(hashFiles: true).Status.ShouldBe(GeneratorCacheStatus.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_download_is_EQD0101_with_dpms_reason_and_no_secrets()
    {
        _runner.On(Dpm, Install, new ProcessResult(1, "Installing components...\n",
            "Error: error handling component \"ghcr.io/peacefulstudio/dpm-codegen-cs\": proxyconnect tcp: http://ann:s3cret@proxy.example:8080 refused\n"));

        var problem = (await Prepare(Options())).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.MissingTool);
        problem.Message.ShouldContain("http://***@proxy.example:8080 refused");
        problem.Message.ShouldNotContain("s3cret");
        _cache.IsMarkedReady.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_download_that_still_doesnt_match_the_pin_is_EQD0106()
    {
        _runner.On(Dpm, Install, spec =>
        {
            _download.CopyHomeTo(spec.Environment["DPM_HOME"]!);
            File.WriteAllText(Path.Combine(_cache.Component, "dpm-codegen-cs.cmd"), "@echo something else\r\n");
            return Task.FromResult(new ProcessResult(0, "", ""));
        });

        var problem = (await Prepare(Options())).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.GeneratorPinConflict);
        problem.Message.ShouldStartWith("After installing, the cache of the C# code generator ghcr.io/peacefulstudio/dpm-codegen-cs:0.6.0 still doesn't match its pin: dpm-codegen-cs.cmd");
        _cache.IsMarkedReady.ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_pin_without_a_windows_build_is_EQD0106_and_nothing_is_downloaded()
    {
        var unsupported = SyntheticGeneratorCache.Create(Path.Combine(_root, "unsupported"), windows: false);
        var cache = new GeneratorCache(Path.Combine(_root, "codegen"), unsupported.Pin);
        unsupported.CopyHomeTo(cache.Home);

        var problem = (await new GeneratorPreparation(_runner, Locks).PrepareAsync(cache, Dpm, Options(), CancellationToken.None)).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.GeneratorPinConflict);
        problem.Message.ShouldContain("has no windows/amd64 build");
        _runner.Calls.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Another_build_holding_the_cache_too_long_is_EQD0105()
    {
        using var other = await FileLock.AcquireAsync(LockNames.Generator(Locks, _download.Pin.Digest), TimeSpan.FromSeconds(5), CancellationToken.None);

        var problem = (await Prepare(Options(lockSeconds: 0.3))).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.LockTimeout);
        problem.Message.ShouldContain("EqDamlLockTimeoutSeconds");
    }

    // Two projects building at once both find the generator missing; one downloads it.
    [Fact, Trait("Category", "Unit")]
    public async Task Two_builds_finding_it_missing_download_it_once()
    {
        InstallSucceeds(delayMilliseconds: 300);

        var results = await Task.WhenAll(Prepare(Options()), Prepare(Options()));

        results.ShouldAllBe(r => r == null);
        _runner.Calls.Count.ShouldBe(1);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_download_past_the_time_limit_is_EQD0110()
    {
        _runner.On(Dpm, Install, new TimeoutException("dpm.cmd ran past its limit."));

        var problem = (await Prepare(Options())).ShouldNotBeNull();

        problem.Code.ShouldBe(DamlDiagnosticCodes.CancelledOrTimedOut);
        problem.Message.ShouldContain("EqDamlProcessTimeoutSeconds");
    }
}
