using System;
using System.IO;
using Daml.Build.Core.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.FileSystem;

public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-fs-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileSystem _fs = new();

    public PhysicalFileSystemTests() => Directory.CreateDirectory(Path.Combine(_root, "pkg"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact, Trait("Category", "Unit")]
    public void Reports_files_and_directories_and_reads_text()
    {
        var file = Path.Combine(_root, "pkg", "daml.yaml");
        File.WriteAllText(file, "name: zażółć\n");

        _fs.FileExists(file).ShouldBeTrue();
        _fs.FileExists(Path.Combine(_root, "pkg")).ShouldBeFalse();
        _fs.DirectoryExists(Path.Combine(_root, "pkg")).ShouldBeTrue();
        _fs.DirectoryExists(file).ShouldBeFalse();
        _fs.ReadAllText(file).ShouldBe("name: zażółć\n");
    }

    [Fact, Trait("Category", "Unit")]
    public void Reading_a_missing_file_throws_an_IO_exception() =>
        Should.Throw<IOException>(() => _fs.ReadAllText(Path.Combine(_root, "missing.yaml")));
}
