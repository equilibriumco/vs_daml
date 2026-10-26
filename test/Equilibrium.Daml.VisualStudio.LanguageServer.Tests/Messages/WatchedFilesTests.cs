using System.Linq;
using Equilibrium.Daml.VisualStudio.LanguageServer.Messages;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Messages;

public class WatchedFilesTests
{
    // Told only of the new name, damlc keeps resolving imports of the old module.
    [Fact, Trait("Category", "Unit")]
    public void Renaming_a_Daml_source_reports_the_old_file_gone_and_the_new_one_created()
    {
        WatchedFiles.ForRename(@"C:\repo\daml\Dep.daml", @"C:\repo\daml\Renamed.daml").ShouldBe(
        [
            new FileEvent("file:///C:/repo/daml/Dep.daml", FileChangeType.Deleted),
            new FileEvent("file:///C:/repo/daml/Renamed.daml", FileChangeType.Created),
        ]);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\daml\Dep.daml", @"C:\repo\daml\Dep.daml.bak", "Deleted file:///C:/repo/daml/Dep.daml")]
    [InlineData(@"C:\repo\daml\Dep.txt", @"C:\repo\daml\Dep.daml", "Created file:///C:/repo/daml/Dep.daml")]
    [InlineData(@"C:\repo\daml\Dep.DAML", @"C:\repo\daml\Other.Daml", "Deleted file:///C:/repo/daml/Dep.DAML, Created file:///C:/repo/daml/Other.Daml")]
    [InlineData(@"C:\my repo\daml\A.daml", @"C:\my repo\daml\B.daml", "Deleted file:///C:/my%20repo/daml/A.daml, Created file:///C:/my%20repo/daml/B.daml")]
    public void Only_the_names_that_are_Daml_sources_are_reported(string from, string to, string expected)
    {
        string.Join(", ", WatchedFiles.ForRename(from, to).Select(e => $"{e.Type} {e.Uri}")).ShouldBe(expected);
    }

    // damlc's own copies of dependencies, the C# generator's work folder, and other files.
    [Theory, Trait("Category", "Unit")]
    [InlineData(@"C:\repo\.daml\dependencies\2.3\lib-abc\Lib.daml", @"C:\repo\.daml\dependencies\2.3\lib-abc\Lib2.daml")]
    [InlineData(@"C:\repo\App\obj\Debug\net10.0\Daml\.daml-work\9de80d6cfe50bada.run\daml\A.daml", @"C:\repo\App\obj\Debug\net10.0\Daml\.daml-work\9de80d6cfe50bada.run\daml\B.daml")]
    [InlineData(@"C:\repo\daml.yaml", @"C:\repo\daml.yaml.bak")]
    public void Build_output_and_other_files_report_nothing(string from, string to)
    {
        WatchedFiles.ForRename(from, to).ShouldBeEmpty();
    }
}
