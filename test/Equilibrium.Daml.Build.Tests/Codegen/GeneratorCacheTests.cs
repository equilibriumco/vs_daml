using System;
using System.IO;
using Equilibrium.Daml.Build.Core.Codegen;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Codegen;

public sealed class GeneratorCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-generator-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static GeneratorCacheState Check(SyntheticGeneratorCache synthetic, bool hashFiles = true) =>
        new GeneratorCache(synthetic.Root, synthetic.Pin).Check(hashFiles);

    [Fact, Trait("Category", "Unit")]
    public void A_complete_cache_is_ready_and_lives_in_a_folder_of_its_own_digest()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        var cache = new GeneratorCache(synthetic.Root, synthetic.Pin);

        cache.Home.ShouldBe(synthetic.Home);
        cache.Component.ShouldBe(synthetic.Component);
        cache.Check(hashFiles: true).Status.ShouldBe(GeneratorCacheStatus.Ready);
        cache.Check(hashFiles: false).Status.ShouldBe(GeneratorCacheStatus.Ready);
    }

    [Fact, Trait("Category", "Unit")]
    public void The_default_root_is_under_local_app_data() =>
        GeneratorCache.DefaultRoot(name => name == "LOCALAPPDATA" ? @"C:\Users\ann\AppData\Local" : null)
            .ShouldBe(@"C:\Users\ann\AppData\Local\DamlVisualStudio\codegen");

    [Fact, Trait("Category", "Unit")]
    public void Nothing_downloaded_yet_is_missing()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        Directory.Delete(synthetic.Home, recursive: true);

        Check(synthetic).Status.ShouldBe(GeneratorCacheStatus.Missing);
    }

    // Two pins never share a home, so another pin's download says nothing about this one.
    [Fact, Trait("Category", "Unit")]
    public void Another_pins_cache_doesnt_count()
    {
        var other = SyntheticGeneratorCache.Create(_root);
        var mine = SyntheticGeneratorCache.Create(Path.Combine(_root, "elsewhere"), variant: "another build");

        new GeneratorCache(other.Root, mine.Pin).Check(hashFiles: true).Status.ShouldBe(GeneratorCacheStatus.Missing);
    }

    // What a build killed while dpm unpacked the component leaves behind.
    [Fact, Trait("Category", "Unit")]
    public void A_file_cut_short_is_damaged_even_without_hashing()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        File.WriteAllText(synthetic.ComponentFile("bin/daml-dar-to-proto.jar"), "PK");

        var state = Check(synthetic, hashFiles: false);

        state.Status.ShouldBe(GeneratorCacheStatus.Damaged);
        state.BlobsDamaged.ShouldBeFalse();
        state.Reason.ShouldBe($"bin/daml-dar-to-proto.jar is 2 bytes, but the pinned component has {SyntheticGeneratorCache.Files["bin/daml-dar-to-proto.jar"].Length}.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_file_with_the_right_size_and_other_content_is_caught_by_hashing()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        File.WriteAllText(synthetic.ComponentFile("bin/Daml.Codegen.CSharp.Cli.exe"), "MZ emitteR");

        Check(synthetic, hashFiles: false).Status.ShouldBe(GeneratorCacheStatus.Ready);
        var state = Check(synthetic, hashFiles: true);
        state.Status.ShouldBe(GeneratorCacheStatus.Damaged);
        state.Reason.ShouldBe("bin/Daml.Codegen.CSharp.Cli.exe doesn't match the pinned component.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_file_is_damaged()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        File.Delete(synthetic.ComponentFile("dpm-codegen-cs.cmd"));

        Check(synthetic, hashFiles: false).Reason.ShouldBe("dpm-codegen-cs.cmd is missing.");
    }

    [Fact, Trait("Category", "Unit")]
    public void An_index_that_isnt_the_pinned_one_damages_the_blobs()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        File.AppendAllText(Path.Combine(synthetic.Blobs, synthetic.Pin.Digest), " ");

        var state = Check(synthetic, hashFiles: false);

        (state.Status, state.BlobsDamaged).ShouldBe((GeneratorCacheStatus.Damaged, true));
        state.Reason.ShouldBe("The downloaded index doesn't match the pinned digest.");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_platform_manifest_damages_the_blobs()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        File.Delete(Path.Combine(synthetic.Blobs, synthetic.ManifestDigest));

        var state = Check(synthetic, hashFiles: false);

        (state.Status, state.BlobsDamaged).ShouldBe((GeneratorCacheStatus.Damaged, true));
        state.Reason.ShouldBe($"The windows/amd64 manifest sha256:{synthetic.ManifestDigest} wasn't downloaded.");
    }

    // Nothing to repair: the pin itself has no build this machine can run.
    [Fact, Trait("Category", "Unit")]
    public void A_pin_without_a_windows_build_is_unsupported()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root, windows: false);

        var state = Check(synthetic);

        state.Status.ShouldBe(GeneratorCacheStatus.Unsupported);
        state.Reason.ShouldBe("The pinned component has no windows/amd64 build.");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_ready_marker_names_the_pin_it_was_written_for()
    {
        var synthetic = SyntheticGeneratorCache.Create(_root);
        var cache = new GeneratorCache(synthetic.Root, synthetic.Pin);

        cache.IsMarkedReady.ShouldBeFalse();
        cache.MarkReady();
        cache.IsMarkedReady.ShouldBeTrue();

        File.WriteAllText(Path.Combine(synthetic.Home, ".daml-ready"), "oci://ghcr.io/peacefulstudio/dpm-codegen-cs:0.5.0@sha256:0000");
        cache.IsMarkedReady.ShouldBeFalse();
    }
}
