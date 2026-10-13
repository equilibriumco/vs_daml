using System;
using System.Collections.Generic;
using Daml.TestHelpers;
using Daml.VisualStudio.Core.Logging;
using Daml.VisualStudio.LanguageServer.Messages;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.LanguageServer.Tests.Messages;

public class LoggingVirtualResourceSinkTests
{
    [Fact, Trait("Category", "Unit")]
    public void Notes_are_logged_as_information()
    {
        var logger = new RecordingLogger(DamlLogLevel.Information);

        new LoggingVirtualResourceSink(logger).Noted(new VirtualResourceNoteParams("daml://compiler?file=Main.daml", "Script failed"));

        var entry = logger.Entries.ShouldHaveSingleItem();
        (entry.Level, entry.Message).ShouldBe((DamlLogLevel.Information, "daml://compiler?file=Main.daml: Script failed"));
    }

    [Fact, Trait("Category", "Unit")]
    public void Changes_and_progress_are_logged_only_when_debug_is_on()
    {
        var quiet = new RecordingLogger(DamlLogLevel.Information);
        var verbose = new RecordingLogger(DamlLogLevel.Debug);

        foreach (var logger in new[] { quiet, verbose })
        {
            var sink = new LoggingVirtualResourceSink(logger);
            sink.Changed(new VirtualResourceChangedParams("daml://a", "<html/>"));
            sink.Progressed(new VirtualResourceProgressParams("daml://a", 1500, 0));
        }

        quiet.Entries.ShouldBeEmpty();
        verbose.Entries.Count.ShouldBe(2);
        verbose.Entries[0].Message.ShouldContain("7 characters");
        verbose.Entries[1].Message.ShouldContain("1500 ms");
    }


}
