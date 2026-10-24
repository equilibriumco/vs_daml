using Equilibrium.Daml.Build.Core.Tools;
using Equilibrium.Daml.TestHelpers;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.Build.Tests.Tools;

// How dpm, java and the daml assistant are found on PATH, the same way for the extension and the build.
public class ToolSearchTests
{
    private readonly InMemoryFileSystem _files = new();

    private string? Find(string name, string? path, string? installFolder = null) => ToolSearch.Find(name, path, installFolder, _files);

    [Fact, Trait("Category", "Unit")]
    public void The_first_PATH_entry_that_has_the_command_wins()
    {
        _files.AddFile(@"C:\later\dpm.exe", "").AddFile(@"C:\first\dpm.cmd", "");

        Find("dpm", @"C:\empty;C:\first;C:\later").ShouldBe(@"C:\first\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void In_one_folder_the_program_comes_before_its_wrappers()
    {
        _files.AddFile(@"C:\tools\dpm.bat", "").AddFile(@"C:\tools\dpm.cmd", "").AddFile(@"C:\tools\dpm.exe", "");

        Find("dpm", @"C:\tools").ShouldBe(@"C:\tools\dpm.exe");
        _files.RemoveFile(@"C:\tools\dpm.exe");
        Find("dpm", @"C:\tools").ShouldBe(@"C:\tools\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_name_with_an_extension_is_looked_for_as_it_is()
    {
        _files.AddFile(@"C:\tools\dpm.exe", "").AddFile(@"C:\tools\dpm.cmd", "");

        Find("dpm.cmd", @"C:\tools").ShouldBe(@"C:\tools\dpm.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void Quoted_and_padded_entries_are_used_and_broken_ones_skipped()
    {
        _files.AddFile(@"C:\Program Files\dpm\dpm.exe", "");

        Find("dpm", "C:\\bad|path; \"C:\\Program Files\\dpm\" ;;").ShouldBe(@"C:\Program Files\dpm\dpm.exe");
    }

    // A relative entry would find a dpm.exe placed in whatever folder the build or Visual Studio
    // happens to run in; only full paths count.
    [Theory, Trait("Category", "Unit")]
    [InlineData(".")]
    [InlineData("bin")]
    [InlineData(@".\bin")]
    [InlineData(@"C:bin")]
    [InlineData(@"\bin")]
    public void Entries_that_arent_full_paths_are_skipped(string entry)
    {
        var everywhere = new EveryFileExists();

        ToolSearch.Find("dpm", entry, null, everywhere).ShouldBeNull();
        everywhere.Asked.ShouldBeEmpty();
    }

    [Fact, Trait("Category", "Unit")]
    public void A_network_share_on_PATH_is_a_full_path()
    {
        _files.AddFile(@"\\server\tools\dpm.exe", "");

        Find("dpm", @"\\server\tools").ShouldBe(@"\\server\tools\dpm.exe");
    }

    [Fact, Trait("Category", "Unit")]
    public void The_install_folder_is_searched_after_PATH()
    {
        _files.AddFile(@"C:\Users\ann\AppData\Roaming\daml\bin\daml.cmd", "").AddFile(@"C:\tools\daml.cmd", "");

        Find("daml", @"C:\tools", @"C:\Users\ann\AppData\Roaming\daml\bin").ShouldBe(@"C:\tools\daml.cmd");
        Find("daml", null, @"C:\Users\ann\AppData\Roaming\daml\bin").ShouldBe(@"C:\Users\ann\AppData\Roaming\daml\bin\daml.cmd");
    }

    [Fact, Trait("Category", "Unit")]
    public void Nothing_found_is_null() => Find("dpm", @"C:\tools;C:\Windows").ShouldBeNull();

    // Answers yes to every file, so what the search asks about is all that decides.
    private sealed class EveryFileExists : Equilibrium.Daml.Build.Core.FileSystem.IDamlFileSystem
    {
        public System.Collections.Generic.List<string> Asked { get; } = [];

        public bool FileExists(string path)
        {
            Asked.Add(path);
            return true;
        }

        public bool DirectoryExists(string path) => true;

        public string ReadAllText(string path) => "";
    }
}
