using System;
using System.IO;
using Equilibrium.Daml.VisualStudio.LanguageServer.Protocol;
using Shouldly;
using Xunit;

namespace Equilibrium.Daml.VisualStudio.LanguageServer.Tests.Protocol;

// The real copier on a real path longer than 260 characters, on both target frameworks.
public sealed class LibraryCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daml-copier-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LongSource()
    {
        var folder = Path.Combine(_root, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(@"\\?\" + folder);
        var source = Path.Combine(folder, "Library.daml");
        source.Length.ShouldBeGreaterThan(260);
        File.WriteAllText(@"\\?\" + source, "module Library where\n");
        return source;
    }

    [Fact, Trait("Category", "Unit")]
    public void A_long_source_is_copied_read_only_and_refreshed_when_it_changes()
    {
        var source = LongSource();
        var target = Path.Combine(_root, "copies", "abc", "Library.daml");
        var copier = new LibraryCopier();

        copier.CopyReadOnly(source, target).ShouldBeTrue();

        File.ReadAllText(target).ShouldBe("module Library where\n");
        File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();

        File.WriteAllText(@"\\?\" + source, "module Library where\nx = 1\n");
        File.SetLastWriteTimeUtc(@"\\?\" + source, DateTime.UtcNow.AddMinutes(1));
        copier.CopyReadOnly(source, target).ShouldBeTrue();

        File.ReadAllText(target).ShouldBe("module Library where\nx = 1\n");
    }

    [Fact, Trait("Category", "Unit")]
    public void A_missing_source_is_not_copied()
    {
        new LibraryCopier().CopyReadOnly(Path.Combine(_root, "nothing.daml"), Path.Combine(_root, "copies", "nothing.daml")).ShouldBeFalse();
    }

    // A package re-extracted from an archive can carry an older timestamp than the copy made
    // from its previous version; the size still gives the change away.
    [Fact, Trait("Category", "Unit")]
    public void A_source_of_another_size_refreshes_the_copy_even_when_it_isnt_newer()
    {
        var source = LongSource();
        var target = Path.Combine(_root, "copies", "abc", "Library.daml");
        var copier = new LibraryCopier();
        copier.CopyReadOnly(source, target).ShouldBeTrue();

        File.WriteAllText(@"\\?\" + source, "module Library where\nlonger = 1\n");
        File.SetLastWriteTimeUtc(@"\\?\" + source, File.GetLastWriteTimeUtc(target).AddMinutes(-5));
        copier.CopyReadOnly(source, target).ShouldBeTrue();

        File.ReadAllText(target).ShouldBe("module Library where\nlonger = 1\n");
    }
    [Fact, Trait("Category", "Unit")]
    public void Same_size_content_with_an_older_timestamp_refreshes_the_copy()
    {
        var source = LongSource();
        var target = Path.Combine(_root, "copies", "Library.daml");
        var copier = new LibraryCopier();
        copier.CopyReadOnly(source, target).ShouldBeTrue();
        File.WriteAllText(@"\\?\" + source, "module Changed where\n");
        File.SetLastWriteTimeUtc(@"\\?\" + source, File.GetLastWriteTimeUtc(target).AddMinutes(-5));

        copier.CopyReadOnly(source, target).ShouldBeTrue();

        File.ReadAllText(target).ShouldBe("module Changed where\n");
    }

}
