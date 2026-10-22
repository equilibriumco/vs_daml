using System;
using System.IO;
using System.Linq;
using Equilibrium.Daml.Build.Core.Building;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Building;

public sealed class CompilerCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-cache-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact, Trait("Category", "Unit")]
    public void Active_commands_are_isolated_and_completed_cache_entries_are_reused()
    {
        string path;
        using (var first = new CompilerCache(_root))
        {
            path = first.Environment["DAML_CACHE"]!;
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "builtin_dar_info.json"), "metadata");
            using var concurrent = new CompilerCache(_root);
            concurrent.Environment["DAML_CACHE"].ShouldNotBe(path);
        }
        using var next = new CompilerCache(_root);
        next.Environment["DAML_CACHE"].ShouldBe(path);
        File.ReadAllText(Path.Combine(path, "builtin_dar_info.json")).ShouldBe("metadata");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_full_pool_uses_a_temporary_cache_and_does_not_wait_or_touch_active_caches()
    {
        var held = Enumerable.Range(0, CompilerCache.Slots).Select(_ => new CompilerCache(_root)).ToArray();
        try
        {
            string path;
            using (var overflow = new CompilerCache(_root))
            {
                path = overflow.Environment["DAML_CACHE"]!;
                held.ShouldAllBe(c => c.Environment["DAML_CACHE"] != path);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "metadata"), "temporary");
            }
            Directory.Exists(path).ShouldBeFalse();
        }
        finally { foreach (var cache in held) { cache.Dispose(); } }
    }
}
