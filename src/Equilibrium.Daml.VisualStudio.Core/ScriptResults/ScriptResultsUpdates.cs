using System;
using System.Threading.Tasks;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// Decides which results a window actually loads. While a script is open the server reruns it
/// on nearly every keystroke and sends results each time, mostly unchanged; each load is a page
/// reload. So only the last of a quick burst gets through, and only if it differs from what the
/// window already shows.
/// </summary>
public sealed class ScriptResultsUpdates : IDisposable
{
    private readonly object _gate = new();
    private readonly Debouncer _debouncer;
    private string? _shown;

    /// <summary>Creates the gate for one window.</summary>
    public ScriptResultsUpdates(TimeSpan delay, TimeProvider time) => _debouncer = new Debouncer(delay, time);

    /// <summary>
    /// Offers new results. Returns them once the burst is over if they are still the latest and
    /// not what is on screen; otherwise, or after <see cref="Dispose"/>, <c>null</c>.
    /// </summary>
    public async Task<string?> SubmitAsync(string html)
    {
        if (html is null)
        {
            throw new ArgumentNullException(nameof(html));
        }
        if (!await _debouncer.WaitAsync().ConfigureAwait(false))
        {
            return null;
        }
        lock (_gate)
        {
            if (string.Equals(html, _shown, StringComparison.Ordinal))
            {
                return null;
            }
            _shown = html;
            return html;
        }
    }

    /// <summary>Forgets what is on screen, e.g. after the page failed to load, so the next results load whatever they are.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _shown = null;
        }
    }

    /// <summary>Drops whatever is waiting; nothing submitted later gets through.</summary>
    public void Dispose() => _debouncer.Dispose();
}
