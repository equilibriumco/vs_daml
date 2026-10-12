using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core;
using Daml.Build.Core.Diagnostics;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Build;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.Core.Processes;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Core.Tests.Build;

public sealed class DamlBuildCoordinatorTests : IDisposable
{
    private readonly FakeDpm _fake;
    private readonly string _dpm;
    private readonly string _package;
    private readonly RecordingLogger _logger = new();

    public DamlBuildCoordinatorTests()
    {
        var scenario = new FakeDpmScenario();
        var broken = new FakeDpmCommand
        {
            Stderr = File.ReadAllText(Path.Combine(Repository.Root, "test", "fixtures", "build-output", "build-broken.stderr.txt")),
            ExitCode = 1,
        };
        broken.Args.Add("build");
        scenario.Commands.Add(broken);
        var clean = new FakeDpmCommand { Stderr = "\u001b[0;91mRemoved\u001b[0m build artifacts.\n" };
        clean.Args.Add("clean");
        scenario.Commands.Add(clean);
        _fake = FakeDpm.Create(scenario);
        _package = Directory.CreateDirectory(Path.Combine(_fake.RootDirectory, "broken")).FullName;

        // The fake reads its scenario from the environment; a wrapper hands it over like dpm.cmd would.
        _dpm = Path.Combine(_fake.RootDirectory, "dpm.cmd");
        var setVariables = string.Concat(_fake.Environment.Select(v => $"set \"{v.Key}={v.Value}\"\r\n"));
        File.WriteAllText(_dpm, "@echo off\r\n" + setVariables + $"\"{_fake.ExecutablePath}\" %*\r\n", Encoding.ASCII);
    }

    public void Dispose() => _fake.Dispose();

    private DamlBuildCoordinator Coordinator(Func<string?>? findDpm = null) =>
        new(new DpmRunner(new ProcessRunner()), findDpm ?? (() => _dpm), _logger);

    [Fact, Trait("Category", "Unit")]
    public async Task A_failed_build_returns_zero_based_diagnostics_and_logs_the_output_one_entry_per_line()
    {
        var run = await Coordinator().RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);

        run.Succeeded.ShouldBeFalse();
        run.Diagnostics.ShouldNotBeEmpty();
        var typeError = run.Diagnostics.First(d => d.FilePath.EndsWith("TypeError.daml", StringComparison.Ordinal));
        typeError.FilePath.ShouldBe(Path.Combine(_package, "daml", "TypeError.daml"));
        typeError.Line.ShouldBeGreaterThanOrEqualTo(0);
        _logger.Lines.ShouldContain(l => l.Level == DamlLogLevel.Information && l.Message.StartsWith("Build", StringComparison.Ordinal) && l.Message.Contains(_package));
        _logger.Lines.ShouldContain(l => l.Message.Contains("File:"));
        _logger.Lines.ShouldContain(l => l.Message.Contains("failed") && l.Message.Contains("exit code 1"));
        // damlc's own log entries arrive as one line each, without its timestamp.
        _logger.Lines.ShouldContain(l => l.Message == "[build] Compiling broken to a DAR.");
        _logger.Lines.ShouldNotContain(l => l.Message.Trim().Length == 0);
        _logger.Lines.ShouldAllBe(l => l.Category == DamlLogCategory.Build);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_clean_reports_no_diagnostics()
    {
        var run = await Coordinator().RunAsync(new DamlBuildAction(DpmCommandKind.Clean, _package, false), CancellationToken.None);

        run.Succeeded.ShouldBeTrue();
        run.Diagnostics.ShouldBeEmpty();
        _logger.Lines.ShouldContain(l => l.Message == "Removed build artifacts."); // ANSI colour codes gone
    }

    [Fact, Trait("Category", "Unit")]
    public async Task Without_dpm_nothing_runs_and_the_log_says_why()
    {
        var run = await Coordinator(() => null).RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);

        run.ShouldBe(DamlBuildRun.NotStarted);
        _logger.Lines.ShouldContain(l => l.Level == DamlLogLevel.Error && l.Message.Contains("DAML0101") && l.Message.Contains("dpm"));
        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task A_cancelled_run_never_starts_dpm()
    {
        var coordinator = Coordinator();
        var cancelled = new CancellationToken(canceled: true);

        await Should.ThrowAsync<OperationCanceledException>(() => coordinator.RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), cancelled));

        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public async Task CancelAll_cancels_a_run_in_progress()
    {
        var gate = Path.Combine(_fake.RootDirectory, "hang.cmd");
        File.WriteAllText(gate, "@echo off\r\nping -n 120 127.0.0.1 >nul\r\n", Encoding.ASCII);
        var coordinator = new DamlBuildCoordinator(new DpmRunner(new ProcessRunner()), () => gate, _logger);

        var run = coordinator.RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);
        await Task.Delay(500);
        coordinator.CancelAll();

        // The run belongs to this test; nothing here is tied to a UI context.
#pragma warning disable VSTHRD003
        await Should.ThrowAsync<OperationCanceledException>(() => run);
#pragma warning restore VSTHRD003
        _logger.Lines.ShouldContain(l => l.Message.Contains("cancelled"));
    }

    private sealed class RecordingLogger : IDamlLogger
    {
        private readonly List<(DamlLogCategory Category, DamlLogLevel Level, string Message)> _lines = new();

        public IReadOnlyList<(DamlLogCategory Category, DamlLogLevel Level, string Message)> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public bool IsEnabled(DamlLogLevel level) => true;

        public void Log(DamlLogCategory category, DamlLogLevel level, string message, Exception? exception = null)
        {
            lock (_lines)
            {
                _lines.Add((category, level, message));
            }
        }
    }
}
