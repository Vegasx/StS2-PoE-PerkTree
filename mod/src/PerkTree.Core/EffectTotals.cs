namespace PerkTree.Core;

/// <summary>One allocated node as the dice see it: what it gives on success, and how likely that is (1 = always).</summary>
public readonly record struct NodeRoll(decimal Amount, decimal Chance);

/// <summary>
/// The summed value of one effect across a set of nodes. Powers the gameplay value, the node tooltip
/// ("Currently 10% (2 perks)") and the summary screen, so all three can never disagree.
/// </summary>
public readonly record struct EffectTotal(
    string EffectId, decimal Raw, decimal? Cap, int NodeCount, IReadOnlyList<NodeRoll>? Rolls = null)
{
    /// <summary>
    /// Each allocated node's own amount and chance, in a FIXED order (by node id). An effect rolls once per node, so
    /// the list is what the game rolls over; the order matters because every co-op client must consume random numbers
    /// in the same sequence, and a set of ids has no order of its own.
    /// </summary>
    public IReadOnlyList<NodeRoll> NodeRolls => Rolls ?? Array.Empty<NodeRoll>();

    /// <summary>What the game should actually apply: the raw sum, clamped to the cap.</summary>
    public decimal Effective => Cap is { } c && Raw > c ? c : Raw;

    /// <summary>True once the cap is reached (not only exceeded), so the UI can say "maxed".</summary>
    public bool AtCap => Cap is { } c && Raw >= c;

    /// <summary>Points of magnitude lost to the cap; the UI should tell the player when this is above zero.</summary>
    public decimal Wasted => Raw - Effective;
}

internal static class EffectMath
{
    /// <summary>Linear, additive stacking (design doc 7.1): sum each effect's node magnitudes.</summary>
    public static Dictionary<string, EffectTotal> Compute(TreeGraph graph, IEnumerable<string> nodeIds)
    {
        var byEffect = new Dictionary<string, List<(string NodeId, decimal Magnitude, decimal Chance)>>(StringComparer.Ordinal);
        foreach (var id in nodeIds)
        {
            var node = graph.Node(id);
            if (!byEffect.TryGetValue(node.EffectId, out var list)) byEffect[node.EffectId] = list = new();
            list.Add((id, node.Magnitude, node.Chance ?? graph.EffectOf(node).Chance));
        }
        return byEffect.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                // Sorted by node id so the order never depends on how the nodes were allocated or loaded.
                kv.Value.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
                var rolls = kv.Value.Select(n => new NodeRoll(n.Magnitude, n.Chance)).ToArray();
                // Raw is the amount the perks give when every roll succeeds (what the cap and the "is this effect active" check use).
                return new EffectTotal(kv.Key, rolls.Sum(r => r.Amount), graph.Effect(kv.Key).Cap, rolls.Length, rolls);
            },
            StringComparer.Ordinal);
    }

    public static EffectTotal Single(TreeGraph graph, IEnumerable<string> nodeIds, string effectId) =>
        Compute(graph, nodeIds).TryGetValue(effectId, out var t)
            ? t
            : new EffectTotal(effectId, 0m, graph.Effect(effectId).Cap, 0);
}
