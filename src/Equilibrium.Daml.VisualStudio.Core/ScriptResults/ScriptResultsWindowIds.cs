using System;
using System.Collections.Generic;

namespace Equilibrium.Daml.VisualStudio.Core.ScriptResults;

/// <summary>
/// Gives every results resource its own window id, the same one for as long as its window is
/// open. The id starts from the resource's hash, so lookups are cheap, but the hash only says
/// where to look: two resources whose hashes collide are told apart by comparing them in full,
/// and the second one moves on to the next free id.
/// </summary>
public sealed class ScriptResultsWindowIds
{
    private readonly object _gate = new();
    private readonly IEqualityComparer<string> _comparer;
    private readonly Dictionary<int, string> _byId = [];

    /// <summary>Creates a registry comparing resources ordinally.</summary>
    public ScriptResultsWindowIds()
        : this(StringComparer.Ordinal)
    {
    }

    /// <summary>Creates a registry with its own idea of which resources are the same.</summary>
    public ScriptResultsWindowIds(IEqualityComparer<string> comparer) =>
        _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));

    /// <summary>The id of <paramref name="resource"/>'s window, assigning one if it has none yet.</summary>
    public int GetOrAdd(string resource)
    {
        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }
        lock (_gate)
        {
            var id = _comparer.GetHashCode(resource) & int.MaxValue;
            int? free = null;
            // Ids are probed in order from the hash; an entry may sit past a slot freed since,
            // so the search goes on to the first id that was never used.
            for (var probe = 0; probe <= _byId.Count + _freed.Count; probe++)
            {
                var candidate = (int)((id + (long)probe) & int.MaxValue);
                if (_byId.TryGetValue(candidate, out var taken))
                {
                    if (_comparer.Equals(taken, resource))
                    {
                        return candidate;
                    }
                }
                else
                {
                    free ??= candidate;
                    if (!_freed.Contains(candidate))
                    {
                        break;
                    }
                }
            }
            var assigned = free ?? id;
            _byId[assigned] = resource;
            _freed.Remove(assigned);
            return assigned;
        }
    }

    /// <summary>The resource whose window has <paramref name="id"/>, if one is open.</summary>
    public bool TryGetResource(int id, out string resource)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var found))
            {
                resource = found;
                return true;
            }
            resource = "";
            return false;
        }
    }

    /// <summary>Lets go of an id whose window closed.</summary>
    public void Remove(int id)
    {
        lock (_gate)
        {
            if (_byId.Remove(id))
            {
                _freed.Add(id);
            }
            if (_byId.Count == 0)
            {
                _freed.Clear();
            }
        }
    }

    // Ids that were in use and aren't now: a probe must not stop at one of them.
    private readonly HashSet<int> _freed = [];
}
