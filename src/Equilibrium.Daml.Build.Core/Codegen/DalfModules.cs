using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Equilibrium.Daml.Build.Core.Codegen;

// Reads only the LF2 archive envelope and module-name tables. Field numbers come from
// com.daml:daml-lf-archive_2.13:3.6.1, daml_lf.proto and daml_lf2.proto.
internal static class DalfModules
{
    public static IReadOnlyList<string> Read(Stream stream, string packageId)
    {
        try { return ReadNames(stream, packageId); }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("The main DALF has invalid name metadata.", ex);
        }
    }

    private static string[] ReadNames(Stream stream, string packageId)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var archive = Fields(new ArraySegment<byte>(buffer.ToArray())).ToList();
        var payload = archive.Single(f => f.Number == 3).Bytes;
        using var sha = SHA256.Create();
        var hash = string.Concat(sha.ComputeHash(payload.Array!, payload.Offset, payload.Count).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        if (hash != packageId)
        {
            throw new InvalidDataException("The main DALF payload does not match its package ID.");
        }
        var lf = Fields(payload).SingleOrDefault(f => f.Number == 4);
        if (lf.Bytes.Array is null)
        {
            return [];
        }
        var fields = Fields(lf.Bytes).ToList();
        var strings = fields.Where(f => f.Number == 2).Select(f => Encoding.UTF8.GetString(f.Bytes.Array!, f.Bytes.Offset, f.Bytes.Count)).ToArray();
        var names = fields.Where(f => f.Number == 3).Select(f => string.Join(".", Fields(f.Bytes)
            .Where(s => s.Number == 1)
            .SelectMany(s => s.Wire == 0 ? new[] { s.Value } : Packed(s.Bytes))
            .Select(i => At(strings, i)))).ToArray();
        return fields.Where(f => f.Number == 1)
            .Select(f => At(names, Fields(f.Bytes).FirstOrDefault(n => n.Number == 1).Value))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static string At(string[] values, ulong index) =>
        index < (ulong)values.Length ? values[(int)index] : throw new InvalidDataException("Invalid DALF name-table index.");

    private static IEnumerable<ulong> Packed(ArraySegment<byte> bytes)
    {
        var offset = bytes.Offset;
        while (offset < bytes.Offset + bytes.Count) { yield return Varint(bytes, ref offset); }
    }

    private readonly record struct Field(int Number, int Wire, ulong Value, ArraySegment<byte> Bytes);

    private static IEnumerable<Field> Fields(ArraySegment<byte> bytes)
    {
        var offset = bytes.Offset;
        var end = offset + bytes.Count;
        while (offset < end)
        {
            var tag = Varint(bytes, ref offset);
            var number = checked((int)(tag >> 3));
            var wire = (int)(tag & 7);
            if (number == 0) { throw new InvalidDataException("Invalid DALF field number."); }
            if (wire == 0)
            {
                yield return new Field(number, wire, Varint(bytes, ref offset), default);
                continue;
            }
            var length = wire switch
            {
                1 => 8UL,
                2 => Varint(bytes, ref offset),
                5 => 4UL,
                _ => throw new InvalidDataException("Unsupported DALF wire type."),
            };
            if (length > (ulong)(end - offset)) { throw new InvalidDataException("Truncated DALF field."); }
            yield return new Field(number, wire, 0, new ArraySegment<byte>(bytes.Array!, offset, (int)length));
            offset += (int)length;
        }
    }

    private static ulong Varint(ArraySegment<byte> bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Offset + bytes.Count) { throw new InvalidDataException("Truncated DALF integer."); }
            var next = bytes.Array![offset++];
            if (shift == 63 && next > 1) { throw new InvalidDataException("DALF integer overflow."); }
            value |= (ulong)(next & 127) << shift;
            if (next < 128) { return value; }
        }
        throw new InvalidDataException("DALF integer overflow.");
    }
}
