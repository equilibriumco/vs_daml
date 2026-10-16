using System;
using System.IO;
using Daml.Build.Core.FileSystem;
using Shouldly;
using Xunit;

namespace Daml.Build.Tests.FileSystem;

public class HashingTests
{
    [Fact, Trait("Category", "Unit")]
    public void Text_and_files_hash_to_lowercase_sha256()
    {
        Hashing.Sha256Text("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        var path = Path.Combine(Path.GetTempPath(), "daml-hash-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "abc");
            Hashing.Sha256File(path).ShouldBe(Hashing.Sha256Text("abc"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
