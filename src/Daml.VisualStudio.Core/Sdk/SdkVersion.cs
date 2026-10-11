using System.Globalization;

namespace Daml.VisualStudio.Core.Sdk;

internal static class SdkVersion
{
    // Compares major.minor of an SDK version like "3.3.0" or "3.3.0-snapshot.20250312.0".
    // Null when the version says nothing about features: unparsable, or a 0.0.0 local build.
    public static bool? IsBefore(string version, int major, int minor)
    {
        var release = version.Split('-', '+')[0].Split('.');
        if (release.Length < 2
            || !int.TryParse(release[0], NumberStyles.None, CultureInfo.InvariantCulture, out var actualMajor)
            || !int.TryParse(release[1], NumberStyles.None, CultureInfo.InvariantCulture, out var actualMinor))
        {
            return null;
        }
        if (actualMajor == 0 && actualMinor == 0)
        {
            return null;
        }
        return actualMajor < major || (actualMajor == major && actualMinor < minor);
    }
}
