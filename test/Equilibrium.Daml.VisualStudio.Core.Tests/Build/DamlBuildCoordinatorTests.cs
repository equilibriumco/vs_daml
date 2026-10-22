using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Equilibrium.Daml.Build.Core;
using Equilibrium.Daml.Build.Core.Diagnostics;
using Equilibrium.Daml.Build.Core.Processes;
using Equilibrium.Daml.TestHelpers;
using Equilibrium.Daml.VisualStudio.Core.Build;
using Equilibrium.Daml.VisualStudio.Core.Diagnostics;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Equilibrium.Daml.VisualStudio.Core.Tests.Diagnostics;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests.Build;

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

    [Fact, Trait("Category", "Process")]
    public async Task Tool_output_credentials_are_masked_in_logs_and_diagnostics()
    {
        var scenario = new FakeDpmScenario();
        var command = new FakeDpmCommand { Stderr = "File: daml/Main.daml\nHidden: no\nRange: 1:1-1:2\nSeverity: DsError\nMessage:\nfetch https://alice:secret@registry/contracts\n", ExitCode = 1 };
        command.Args.Add("build");
        scenario.Commands.Add(command);
        _fake.SetScenario(scenario);

        var run = await Coordinator().RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);
        _logger.Entries.ShouldContain(e => e.Message.Contains("https://***@registry/contracts"));
        _logger.Entries.ShouldAllBe(e => !e.Message.Contains("secret"));
        run.Diagnostics.ShouldNotBeEmpty();
        run.Diagnostics.ShouldAllBe(d => !d.Message.Contains("secret"));
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_failed_build_returns_zero_based_diagnostics_and_logs_the_output_one_entry_per_line()
    {
        var run = await Coordinator().RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);

        run.Succeeded.ShouldBeFalse();
        run.Diagnostics.ShouldNotBeEmpty();
        var typeError = run.Diagnostics.First(d => d.FilePath.EndsWith("TypeError.daml", StringComparison.Ordinal));
        typeError.FilePath.ShouldBe(Path.Combine(_package, "daml", "TypeError.daml"));
        (typeError.Line, typeError.Column).ShouldBe((4, 8), "5:9 in damlc's 1-based output");
        _logger.Entries.ShouldContain(l => l.Level == DamlLogLevel.Information && l.Message.StartsWith("Build", StringComparison.Ordinal) && l.Message.Contains(_package));
        _logger.Entries.ShouldContain(l => l.Message.Contains("File:"));
        _logger.Entries.ShouldContain(l => l.Message.Contains("failed") && l.Message.Contains("exit code 1"));
        // damlc's own log entries arrive as one line each, without its timestamp.
        _logger.Entries.ShouldContain(l => l.Message == "[build] Compiling broken to a DAR.");
        _logger.Entries.ShouldNotContain(l => l.Message.Trim().Length == 0);
        _logger.Entries.ShouldAllBe(l => l.Category == DamlLogCategory.Build);
    }

    [Theory, Trait("Category", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compiler_code_is_masked_from_logged_build_output_and_flattened_diagnostics(bool flattened)
    {
        using var coordinator = Coordinator();
        var run = await coordinator.RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);
        var folder = Path.Combine(_fake.RootDirectory, "logs");
        var sink = new LogFileSink(new RollingLogFile(folder, 7, TimeProvider.System), TimeProvider.System);
        if (flattened)
        {
            // CurrentPackage passes these lines to WritePlainLine, which writes them to this sink.
            foreach (var diagnostic in run.Diagnostics)
            {
                sink.Write(DamlLogCategory.Build, DamlLogLevel.Information, diagnostic.ToOutputLine());
            }
        }
        else
        {
            foreach (var entry in _logger.Entries)
            {
                sink.Write(entry.Category, entry.Level, entry.Message);
            }
        }
        await sink.DrainAsync();
        File.ReadAllText(RollingLogFile.Files(folder).Single().FullName).ShouldContain("forty-two");

        var report = DiagnosticReport.Create(new DiagnosticReportInput { LogFolder = folder },
            DiagnosticRedactorTests.Redactor, TimeProvider.System);

        report.ShouldNotContain("forty-two");
        report.ShouldNotContain("total =");
        report.ShouldContain("TypeError.daml");
        report.ShouldContain(flattened ? "(5,9): error EQD0001:" : "TypeError.daml:5:9: error:");
        report.ShouldContain("Couldn't match expected type ‘Int’ with actual type ‘Text’");
        report.ShouldContain("In the expression: <redacted code>");
        report.ShouldContain("info [Build]");
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_clean_reports_no_diagnostics()
    {
        var run = await Coordinator().RunAsync(new DamlBuildAction(DpmCommandKind.Clean, _package, false), CancellationToken.None);

        run.Succeeded.ShouldBeTrue();
        run.Diagnostics.ShouldBeEmpty();
        _logger.Entries.ShouldContain(l => l.Message == "Removed build artifacts."); // ANSI colour codes gone
    }

    [Fact, Trait("Category", "Process")]
    public async Task Without_dpm_nothing_runs_and_the_log_says_why()
    {
        var run = await Coordinator(() => null).RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), CancellationToken.None);

        run.ShouldBe(DamlBuildRun.NotStarted);
        _logger.Entries.ShouldContain(l => l.Level == DamlLogLevel.Error && l.Message.Contains("EQD0101") && l.Message.Contains("dpm"));
        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
    public async Task A_cancelled_run_never_starts_dpm()
    {
        var coordinator = Coordinator();
        var cancelled = new CancellationToken(canceled: true);

        await Should.ThrowAsync<OperationCanceledException>(() => coordinator.RunAsync(new DamlBuildAction(DpmCommandKind.Build, _package, false), cancelled));

        _fake.ReadCalls().ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Process")]
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
        _logger.Entries.ShouldContain(l => l.Message.Contains("cancelled"));
    }


}
