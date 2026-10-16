using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Daml.Build.Core.FileSystem;

/// <summary>SHA-256 as the build writes it everywhere: lowercase hex.</summary>
public static class Hashing
{
    /// <summary>The SHA-256 of <paramref name="text"/> in UTF-8.</summary>
    public static string Sha256Text(string text)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text)))));
    }

    /// <summary>The SHA-256 of the file at <paramref name="path"/>.</summary>
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path ?? throw new ArgumentNullException(nameof(path)));
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(stream));
    }

    /// <summary>Lowercase hex of <paramref name="bytes"/>.</summary>
    public static string Hex(byte[] bytes)
    {
        var hex = new StringBuilder((bytes ?? throw new ArgumentNullException(nameof(bytes))).Length * 2);
        foreach (var b in bytes)
        {
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return hex.ToString();
    }
}
