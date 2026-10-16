using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Daml.Build.Core.Tools;

// What a tool printed when asked its version, for the life of the process, keyed by the file's
// path, size and time: a build of many projects asks each tool once, and updating the tool
// changes the key. Only answers of a run that succeeded are kept.
internal sealed class ToolVersionCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _known = new(StringComparer.OrdinalIgnoreCase);

    public static ToolVersionCache Shared { get; } = new();

    public async Task<(IReadOnlyList<string> Output, bool Succeeded)> OutputAsync(string tool, Func<Task<(IReadOnlyList<string> Output, bool Succeeded)>> ask)
    {
        var file = new FileInfo(tool);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        if (_known.TryGetValue(key, out var known))
        {
            return (known, true);
        }
        var answer = await ask().ConfigureAwait(false);
        if (answer.Succeeded)
        {
            _known[key] = answer.Output;
        }
        return answer;
    }
}
