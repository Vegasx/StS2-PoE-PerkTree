using System.Buffers.Binary;
using System.Text;

namespace PerkTree.Core;

/// <summary>
/// Named run-long counters for one player ("attacks played so far", "turns taken so far"). They are part of the player's
/// perk state, so they are saved in the sidecar with everything else and survive combats, quitting and reloading.
///
/// CO-OP: a counter is never sent over the network. Every client counts the same synced game events, in the same order,
/// so every client holds the same value. That only holds if (a) a counter is changed only from inside a game hook, never
/// from UI or timers, and (b) every client started from the same value, which <see cref="Hash"/> exists to verify.
/// </summary>
public sealed class PerkCounters
{
    private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);

    public long Get(string key) => _values.TryGetValue(key, out var v) ? v : 0;

    /// <summary>Adds to a counter and returns the new value.</summary>
    public long Add(string key, long amount = 1)
    {
        var next = Get(key) + amount;
        _values[key] = next;
        return next;
    }

    /// <summary>Sorted copy of every non-zero counter, in the order they are saved.</summary>
    public SortedDictionary<string, long> ToSave()
    {
        var sorted = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in _values)
            if (value != 0) sorted[key] = value;
        return sorted;
    }

    /// <summary>
    /// Restores saved counters. Negative values can only come from a hand-edited or corrupt file, so they are dropped
    /// rather than trusted. Keys for effects that no longer exist are kept: they are harmless and may come back.
    /// </summary>
    public static PerkCounters FromSave(IReadOnlyDictionary<string, long>? saved)
    {
        var counters = new PerkCounters();
        if (saved is null) return counters;
        foreach (var (key, value) in saved)
            if (!string.IsNullOrEmpty(key) && value > 0) counters._values[key] = value;
        return counters;
    }

    /// <summary>
    /// Stable hash of every counter, independent of insertion order, for co-op peers to compare. Zero-valued counters are
    /// skipped so "never touched" and "touched but back at zero" hash the same.
    /// </summary>
    public ulong Hash()
    {
        // FNV-1a by hand: string.GetHashCode is randomized per process, so it cannot be compared across machines.
        ulong hash = 14695981039346656037UL;
        void Mix(byte b) { hash ^= b; hash *= 1099511628211UL; }

        var bytes = new byte[8];
        foreach (var (key, value) in ToSave())
        {
            foreach (var b in Encoding.UTF8.GetBytes(key)) Mix(b);
            Mix((byte)'=');
            BinaryPrimitives.WriteInt64BigEndian(bytes, value); // fixed byte order whatever the machine
            foreach (var b in bytes) Mix(b);
            Mix((byte)'\n');
        }
        return hash;
    }
}
