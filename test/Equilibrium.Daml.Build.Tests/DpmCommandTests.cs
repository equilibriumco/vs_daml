using System;
using Equilibrium.Daml.Build.Core;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests;

public class DpmCommandTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData(DpmCommandKind.Build, false, new[] { "build" })]
    [InlineData(DpmCommandKind.Build, true, new[] { "build", "--all" })]
    [InlineData(DpmCommandKind.Clean, false, new[] { "clean" })]
    [InlineData(DpmCommandKind.Clean, true, new[] { "clean", "--all" })]
    [InlineData(DpmCommandKind.Test, false, new[] { "test" })]
    public void Each_command_maps_to_its_dpm_arguments(DpmCommandKind kind, bool allPackages, string[] expected) =>
        DpmCommand.Arguments(kind, allPackages).ShouldBe(expected);

    [Fact, Trait("Category", "Unit")]
    public void Testing_every_package_at_once_is_refused() =>
        Should.Throw<ArgumentException>(() => DpmCommand.Arguments(DpmCommandKind.Test, allPackages: true));

    [Fact, Trait("Category", "Unit")]
    public void An_unknown_command_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => DpmCommand.Arguments((DpmCommandKind)42, allPackages: false));
}
