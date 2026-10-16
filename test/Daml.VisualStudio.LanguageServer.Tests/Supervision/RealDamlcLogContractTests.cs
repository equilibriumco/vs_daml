using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daml.Build.Core.Processes;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageServer.Supervision;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Supervision;

// The Output window shows damlc's log one entry per line; this breaks if damlc changes how it
// writes entries to stderr.
public class RealDamlcLogContractTests
{
    [SkippableFact, Trait("Category", "Contract"), Trait("RequiresDaml", "true")]
    public async Task Damlc_log_entries_reach_the_logger_whole_and_without_their_own_timestamps()
    {
        var dpm = RequiredTools.Require(ExternalTool.Dpm);
        var command = new ProcessSpec(dpm, ["damlc", "multi-ide", "--log-level=INFO"]) { WorkingDirectory = Repository.Sample("single-package") };
        var logger = new RecordingLogger();
        using var supervisor = new LanguageServerSupervisor(new ProcessRunner(), _ => Task.FromResult(command), logger, TimeProvider.System);

        await supervisor.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!logger.Entries.Any(e => e.Message.Contains("Updating package data")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        await supervisor.StopAsync(CancellationToken.None, TimeSpan.Zero);

        var entry = logger.Entries.First(e => e.Message.Contains("Updating package data"));
        entry.Message.ShouldBe("[Multi-IDE] Updating package data");
        entry.Level.ShouldBe(DamlLogLevel.Information);
        logger.Entries.ShouldNotContain(e => e.Message.Trim().Length == 0);
        logger.Entries.ShouldNotContain(e => e.Message.StartsWith("20", StringComparison.Ordinal) && e.Message.Contains("[INFO]"));
    }


}
