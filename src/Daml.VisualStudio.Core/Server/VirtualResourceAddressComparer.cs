using System;
using System.Collections.Generic;

namespace Daml.VisualStudio.Core.Server;

/// <summary>
/// Compares resource addresses as text the way <see cref="VirtualResourceUri"/> compares them,
/// so the same script's results reached through two spellings of its file find one window.
/// Text that isn't a <c>daml://compiler</c> address is compared ordinally.
/// </summary>
public sealed class VirtualResourceAddressComparer : IEqualityComparer<string>
{
    /// <summary>The one instance; it has no state.</summary>
    public static VirtualResourceAddressComparer Instance { get; } = new();

    private VirtualResourceAddressComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(string? x, string? y)
    {
        if (VirtualResourceUri.TryParse(x, out var a) && VirtualResourceUri.TryParse(y, out var b))
        {
            return a!.Equals(b);
        }
        return string.Equals(x, y, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public int GetHashCode(string obj) =>
        VirtualResourceUri.TryParse(obj, out var uri) ? uri!.GetHashCode() : StringComparer.Ordinal.GetHashCode(obj ?? "");
}
