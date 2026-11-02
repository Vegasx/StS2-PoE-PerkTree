using System.Text;

namespace PerkTree.Core;

public enum PerkOpResult
{
    Ok,
    WindowClosed,
    NoPoints,
    UnknownNode,
    AlreadyAllocated,
    NotConnected,
    NotPending,
    WouldDisconnect,
}

/// <summary>Why a received commit was refused. Every client reaches the same answer from the same list, so a refusal is never a desync.</summary>
public enum PerkCommitResult
{
    Ok,
    TooManyNodes,
    UnknownNode,
    DuplicateNode,
    AlreadyCommitted,
    NotConnected,
    NotEnoughPoints,
}

/// <summary>What happened when a save was loaded against a tree that may have changed since.</summary>
public sealed class PerkLoadReport
{
    public List<string> DroppedNodes { get; } = new();
    public int RefundedPoints => DroppedNodes.Count;
}

/// <summary>Serializable per-player state. Pending nodes are saved so quitting mid-allocation neither commits nor loses them.</summary>
public sealed class PerkSaveData
{
    public int Version { get; init; } = 1;
    public List<string> Committed { get; init; } = new();
    public List<string> Pending { get; init; } = new();

    /// <summary>Points the player holds right now, NOT counting points already spent on pending nodes.</summary>
    public int Unspent { get; init; }

    /// <summary>Run-long counters (see <see cref="PerkCounters"/>). Absent in saves from before counters existed, which just means "all zero".</summary>
    public SortedDictionary<string, long> Counters { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One player's perk progress for one run (design doc sections 2, 5, 6). Pure state machine, no game
/// types: the host mod calls <see cref="AwardPoints"/> on combat win, <see cref="OpenAllocationWindow"/>
/// when the map screen opens and <see cref="Commit"/> when the player votes for the next node.
/// </summary>
public sealed class PerkState
{
    private readonly TreeGraph _graph;
    private readonly HashSet<string> _committed = new(StringComparer.Ordinal);

    // A list, not a set: refunds are validated against the whole allocated set, and order is kept
    // purely so the UI can show pending nodes in the sequence the player picked them.
    private readonly List<string> _pending = new();

    public PerkState(TreeGraph graph) => _graph = graph;

    /// <summary>
    /// Run-long counters for effects that count across combats. Changed only from game hooks, so every co-op client
    /// holds the same values without them being sent anywhere.
    /// </summary>
    public PerkCounters Counters { get; private set; } = new();

    public TreeGraph Graph => _graph;
    public int UnspentPoints { get; private set; }

    /// <summary>
    /// Bumps whenever the committed set changes. Hot paths (damage calculation) cache per-effect totals and compare
    /// this number instead of recomputing the sums on every call.
    /// </summary>
    public int CommittedVersion { get; private set; }

    /// <summary>
    /// True only between the map screen opening and the player voting. It is a flag rather than a screen
    /// type so Core stays free of game concepts, and so "allocation only on the map screen" is enforced
    /// here instead of trusting every UI entry point to remember it.
    /// </summary>
    public bool AllocationOpen { get; private set; }

    public IReadOnlyCollection<string> Committed => _committed;
    public IReadOnlyList<string> Pending => _pending;

    public bool IsAllocated(string nodeId) => _committed.Contains(nodeId) || _pending.Contains(nodeId);

    // ---------------------------------------------------------------- points & window

    public void AwardPoints(int points)
    {
        if (points < 0) throw new ArgumentOutOfRangeException(nameof(points), "Points to award cannot be negative.");
        UnspentPoints += points;
    }

    public void AwardForCombat(RoomKind kind) => AwardPoints(PointRules.ForCombat(kind));

    public void OpenAllocationWindow() => AllocationOpen = true;

    /// <summary>Stops further allocation without committing anything (the player has voted; their commit is still in flight).</summary>
    public void CloseAllocationWindow() => AllocationOpen = false;

    // ---------------------------------------------------------------- allocate / refund / commit

    public PerkOpResult CanAllocate(string nodeId)
    {
        if (!_graph.TryGetNode(nodeId, out _)) return PerkOpResult.UnknownNode;
        if (!AllocationOpen) return PerkOpResult.WindowClosed;
        if (IsAllocated(nodeId)) return PerkOpResult.AlreadyAllocated;
        if (UnspentPoints < PointRules.NodeCost) return PerkOpResult.NoPoints;
        // PoE-style adjacency: a node needs an allocated neighbor, unless it is an entry node.
        if (!_graph.IsStart(nodeId) && !_graph.Neighbors(nodeId).Any(IsAllocated)) return PerkOpResult.NotConnected;
        return PerkOpResult.Ok;
    }

    public PerkOpResult TryAllocate(string nodeId)
    {
        var result = CanAllocate(nodeId);
        if (result != PerkOpResult.Ok) return result;
        _pending.Add(nodeId);
        UnspentPoints -= PointRules.NodeCost;
        return PerkOpResult.Ok;
    }

    /// <summary>
    /// Free refund of a pending node (v1 respec). Refused if another allocated node depends on it to stay
    /// linked to a start, otherwise the player could strand nodes the adjacency rule would never have allowed.
    /// </summary>
    public PerkOpResult TryRefundPending(string nodeId)
    {
        if (!AllocationOpen) return PerkOpResult.WindowClosed;
        if (!_pending.Contains(nodeId)) return PerkOpResult.NotPending;

        var remaining = _committed.Concat(_pending.Where(p => p != nodeId)).ToList();
        if (_graph.ReachableFromStarts(remaining).Count != remaining.Count) return PerkOpResult.WouldDisconnect;

        _pending.Remove(nodeId);
        UnspentPoints += PointRules.NodeCost;
        return PerkOpResult.Ok;
    }

    /// <summary>Refunds every pending node at once (always safe: committed nodes never depend on pending ones). Used by a "reset" button and by cleanup.</summary>
    public int RefundAllPending()
    {
        int n = _pending.Count;
        UnspentPoints += n * PointRules.NodeCost;
        _pending.Clear();
        return n;
    }

    /// <summary>
    /// Called when this player votes for the next map node: pending nodes become permanent and the window
    /// closes. Runs even with nothing pending, because voting locks you regardless.
    /// </summary>
    public int Commit()
    {
        int n = _pending.Count;
        foreach (var id in _pending) _committed.Add(id);
        _pending.Clear();
        AllocationOpen = false;
        if (n > 0) CommittedVersion++;
        return n;
    }

    /// <summary>The most nodes one commit may carry. A real commit is bounded by the points a player can hold; this just caps a corrupt or hostile packet.</summary>
    public const int MaxCommitNodes = 64;

    /// <summary>
    /// Applies a player's lock as it arrived over the network, and does the same thing on every client including the
    /// sender's: any local pending nodes are refunded first, so each machine starts from the same baseline, then the
    /// list is checked against the points and tree this machine already holds and committed in one step.
    ///
    /// It deliberately does NOT look at <see cref="AllocationOpen"/>: that flag follows each client's own screen timing,
    /// and a result that depended on it could differ between machines. Points, committed nodes and the tree are all
    /// synced state, so the answer is the same everywhere. Order matters (a node must touch a start or an earlier node),
    /// and a refused list commits nothing at all.
    /// </summary>
    public PerkCommitResult TryCommitExact(IReadOnlyList<string> nodeIds)
    {
        RefundAllPending();
        AllocationOpen = false; // voting locks the player whatever the outcome

        if (nodeIds.Count > MaxCommitNodes) return PerkCommitResult.TooManyNodes;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reached = new HashSet<string>(_committed, StringComparer.Ordinal);
        var points = UnspentPoints;
        foreach (var id in nodeIds)
        {
            if (!_graph.TryGetNode(id, out _)) return PerkCommitResult.UnknownNode;
            if (_committed.Contains(id)) return PerkCommitResult.AlreadyCommitted;
            if (!seen.Add(id)) return PerkCommitResult.DuplicateNode;
            if (points < PointRules.NodeCost) return PerkCommitResult.NotEnoughPoints;
            if (!_graph.IsStart(id) && !_graph.Neighbors(id).Any(reached.Contains)) return PerkCommitResult.NotConnected;
            reached.Add(id);
            points -= PointRules.NodeCost;
        }

        foreach (var id in nodeIds) _committed.Add(id);
        UnspentPoints = points;
        if (nodeIds.Count > 0) CommittedVersion++;
        return PerkCommitResult.Ok;
    }

    // ---------------------------------------------------------------- totals

    /// <summary>
    /// Per-effect totals. Gameplay must use committed only (the default); pass includePending for the tree
    /// UI so it can show what the player is about to have.
    /// </summary>
    public IReadOnlyDictionary<string, EffectTotal> GetTotals(bool includePending = false) =>
        EffectMath.Compute(_graph, includePending ? _committed.Concat(_pending) : _committed);

    public EffectTotal GetTotal(string effectId, bool includePending = false) =>
        EffectMath.Single(_graph, includePending ? _committed.Concat(_pending) : _committed, effectId);

    /// <summary>
    /// For the hover text "Discard chance 25% -> 50%": the node's effect before and after taking it, relative
    /// to committed + pending. Ignores points and adjacency on purpose, so locked nodes can still be previewed.
    /// </summary>
    public (EffectTotal Before, EffectTotal After) PreviewAllocate(string nodeId)
    {
        var node = _graph.Node(nodeId);
        var current = _committed.Concat(_pending).ToList();
        var before = EffectMath.Single(_graph, current, node.EffectId);
        if (IsAllocated(nodeId)) return (before, before);
        current.Add(nodeId);
        return (before, EffectMath.Single(_graph, current, node.EffectId));
    }

    // ---------------------------------------------------------------- save / load / sync

    public PerkSaveData ToSave() => new()
    {
        // Sorted so identical states always serialize identically (diffable saves, stable hashes).
        Committed = _committed.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        Pending = _pending.ToList(),
        Unspent = UnspentPoints,
        Counters = Counters.ToSave(),
    };

    /// <summary>
    /// Restores a save against the current tree. Nodes that no longer exist, or that a tree edit has cut off
    /// from every start, are dropped and their points refunded, so a data update never corrupts a run in progress.
    /// </summary>
    public static PerkState FromSave(TreeGraph graph, PerkSaveData save, out PerkLoadReport report)
    {
        var rep = new PerkLoadReport();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();

        foreach (var id in save.Committed)
            if (graph.TryGetNode(id, out _)) committed.Add(id); else dropped.Add(id);
        foreach (var id in save.Pending)
        {
            if (!graph.TryGetNode(id, out _)) dropped.Add(id);
            else if (!committed.Contains(id) && !pending.Contains(id)) pending.Add(id);
        }

        var reached = graph.ReachableFromStarts(committed.Concat(pending));
        foreach (var id in committed.Where(id => !reached.Contains(id)).ToList())
        {
            committed.Remove(id);
            dropped.Add(id);
        }
        pending.RemoveAll(id =>
        {
            if (reached.Contains(id)) return false;
            dropped.Add(id);
            return true;
        });

        var state = new PerkState(graph);
        foreach (var id in committed) state._committed.Add(id);
        state._pending.AddRange(pending);
        state.Counters = PerkCounters.FromSave(save.Counters);
        state.CommittedVersion = 1; // differs from a fresh state's 0, so a cache built before the load is never trusted
        // Refund one point per dropped node so nobody loses progress to a balance patch.
        state.UnspentPoints = Math.Max(0, save.Unspent) + dropped.Count * PointRules.NodeCost;

        rep.DroppedNodes.AddRange(dropped.OrderBy(x => x, StringComparer.Ordinal));
        report = rep;
        return state;
    }

    /// <summary>
    /// Stable hash of the committed set, independent of allocation order. Co-op peers can compare these
    /// after a lock to detect that their simulations have diverged before it turns into a desync.
    /// </summary>
    public ulong CommittedHash()
    {
        // FNV-1a, hand-rolled because string.GetHashCode is randomized per process and so useless across peers.
        ulong hash = 14695981039346656037UL;
        foreach (var id in _committed.OrderBy(x => x, StringComparer.Ordinal))
        {
            foreach (var b in Encoding.UTF8.GetBytes(id)) { hash ^= b; hash *= 1099511628211UL; }
            hash ^= (byte)'\n'; // separator so ["ab","c"] and ["a","bc"] differ
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>
    /// One hash over everything co-op peers must agree on before a combat: the committed nodes and the counters.
    /// Combines the two hashes with a fixed mix, so equal states hash equal on every machine.
    /// </summary>
    public ulong SyncHash()
    {
        ulong hash = 14695981039346656037UL;
        foreach (var part in new[] { CommittedHash(), Counters.Hash() })
            for (var shift = 56; shift >= 0; shift -= 8) { hash ^= (byte)(part >> shift); hash *= 1099511628211UL; }
        return hash;
    }
}
