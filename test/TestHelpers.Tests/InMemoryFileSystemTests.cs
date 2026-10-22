using System.IO;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.TestHelpers.Tests;

public class InMemoryFileSystemTests
{
    private readonly InMemoryFileSystem _fs = new();

    [Fact, Trait("Category", "Unit")]
    public void A_file_implies_its_parent_directories()
    {
        _fs.AddFile(@"C:\work\repo\pkg\daml.yaml", "name: x");

        _fs.FileExists(@"c:\WORK\repo\pkg\daml.yaml").ShouldBeTrue();
        _fs.DirectoryExists(@"C:\work\repo\pkg").ShouldBeTrue();
        _fs.DirectoryExists(@"C:\work").ShouldBeTrue();
        _fs.DirectoryExists(@"C:\work\repo\pkg\daml.yaml").ShouldBeFalse();
        _fs.ReadAllText(@"C:/work/repo/pkg/daml.yaml").ShouldBe("name: x");
    }

    [Fact, Trait("Category", "Unit")]
    public void Empty_directories_can_be_added_and_files_removed()
    {
        _fs.AddDirectory(@"C:\work\empty");
        _fs.AddFile(@"C:\work\a.txt", "a");
        _fs.RemoveFile(@"C:\work\a.txt");

        _fs.DirectoryExists(@"C:\work\empty").ShouldBeTrue();
        _fs.FileExists(@"C:\work\a.txt").ShouldBeFalse();
        Should.Throw<FileNotFoundException>(() => _fs.ReadAllText(@"C:\work\a.txt"));
    }
}
