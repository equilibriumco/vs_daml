using System;
using Equilibrium.Daml.VisualStudio.Core.Logging;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.Core.Tests;

public class NullDamlLoggerTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(DamlLogLevel.Trace)]
    [InlineData(DamlLogLevel.Debug)]
    [InlineData(DamlLogLevel.Information)]
    [InlineData(DamlLogLevel.Warning)]
    [InlineData(DamlLogLevel.Error)]
    public void Reports_every_level_as_disabled(DamlLogLevel level)
    {
        NullDamlLogger.Instance.IsEnabled(level).ShouldBeFalse();
    }

    [Fact, Trait("Category", "Unit")]
    public void Accepts_entries_without_throwing()
    {
        Should.NotThrow(() => NullDamlLogger.Instance.Log(DamlLogCategory.Build, DamlLogLevel.Error, "boom", new InvalidOperationException()));
    }
}
