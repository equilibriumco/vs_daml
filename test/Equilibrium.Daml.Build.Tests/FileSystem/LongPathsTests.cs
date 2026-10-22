using System;
using System.IO;
using Equilibrium.Daml.Build.Core.FileSystem;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.FileSystem;

public sealed class LongPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-long-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => DeepPath.Delete(_root);

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\work\App", @"\\?\C:\work\App")]
    [InlineData(@"\\server\share\App", @"\\?\UNC\server\share\App")]
    [InlineData(@"\\?\C:\work\App", @"\\?\C:\work\App")]
    public void A_full_path_has_an_extended_form(string path, string extended) =>
        LongPaths.Extended(path).ShouldBe(extended);

    [Fact, Trait("Category", "Unit")]
    public void Only_a_path_too_long_for_Windows_is_extended()
    {
        var limit = @"C:\" + new string('a', 244);
        var past = limit + "a";

        (LongPaths.Safe(@"C:\work\App.cs"), LongPaths.Safe(limit), LongPaths.Safe(past)).ShouldBe((@"C:\work\App.cs", limit, @"\\?\" + past));
    }

    // The test hosts are .NET 10 and .NET Framework 4.8 apps, both with the new path rules.
    [Fact, Trait("Category", "Unit")]
    public void This_process_takes_extended_paths() => LongPaths.ExtendedPathsWork.ShouldBeTrue();

    // As in the 32-bit MSBuild.exe, built for .NET Framework 4.6 and so on the old path rules.
    [Fact, Trait("Category", "Unit")]
    public void Where_extended_paths_dont_work_paths_stay_plain()
    {
        var past = @"C:\" + new string('a', 245);
        LongPaths.Safe(past, extended: false).ShouldBe(past);

        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Asset")).FullName, "Asset.cs"), "class Asset { }");
        LongPaths.RelativeFiles(_root, extended: false).ShouldBe([@"Asset\Asset.cs"]);
        LongPaths.DeleteFolder(_root, extended: false);
        Directory.Exists(_root).ShouldBeFalse();
    }

    // .NET Framework, as MSBuild.exe runs the tasks, takes such a path only in its extended form.
    [Fact, Trait("Category", "Unit")]
    public void A_file_past_the_limit_is_listed_hashed_and_deleted()
    {
        var path = DeepPath.Write(_root, DeepPath.Relative, "class Registration { }");
        path.Length.ShouldBeGreaterThan(LongPaths.MaxPathLength);

        LongPaths.RelativeFiles(_root).ShouldBe([DeepPath.Relative]);
        LongPaths.FileExists(path).ShouldBeTrue();
        Hashing.Sha256File(path).ShouldBe(Hashing.Sha256Text("class Registration { }"));

        LongPaths.DeleteFolder(_root);
        Directory.Exists(_root).ShouldBeFalse();
    }
}
