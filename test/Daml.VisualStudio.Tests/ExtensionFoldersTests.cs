using System;
using System.IO;
using Daml.VisualStudio.ScriptResults;
using Shouldly;
using Xunit;

namespace Daml.VisualStudio.Tests;

public class ExtensionFoldersTests
{
    // Everything the extension writes for itself goes under one folder, which the user guide names
    // as the one to delete after uninstalling.
    [Fact, Trait("Category", "Editor")]
    public void The_extension_keeps_its_data_in_one_folder()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DamlVisualStudio");

        ExtensionFolders.Data.ShouldBe(data);
        ExtensionFolders.LibraryCopies.ShouldBe(Path.Combine(data, "library"));
        ScriptResultsWindows.DataFolder.ShouldBe(data);
    }
}
