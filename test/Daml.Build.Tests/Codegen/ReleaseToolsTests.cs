using System;
using Daml.Build.Core.Codegen;
using Daml.Build.Core.Configuration;
using Daml.Build.Core.Tools;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.Codegen;

public class ReleaseToolsTests
{
    // One place for what a release was tested with; everything else refers to it.
    [Fact, Trait("Category", "Unit")]
    public void The_release_pins_what_it_was_tested_with()
    {
        ReleaseTools.CodegenComponent.Digest.ShouldBe("9f040219936298f085e2ade77fbbc37829df065b879633282aa4628c9a1b6548");
        ReleaseTools.CodegenComponent.Reference.ShouldBe(DamlProperties.CodegenComponent.Default);
        ReleaseTools.MinimumDpm.ShouldBe(new Version(1, 0, 22));
        DpmVersion.Minimum.ShouldBe(ReleaseTools.MinimumDpm);
        ReleaseTools.MinimumJava.ShouldBe(17);
        (ReleaseTools.Runtime.Package, ReleaseTools.Runtime.Line).ShouldBe(("Daml.Runtime", new Version(0, 6)));
        ReleaseTools.MinimumTargetFramework.ShouldBe(new Version(10, 0));
        ReleaseTools.GenerationContract.ShouldBe(1);
    }
}
