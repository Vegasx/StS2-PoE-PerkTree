using MegaCrit.Sts2.Core.Entities.Players;

namespace PerkTree.Mod;

/// <summary>Loads and caches each character's validated tree from the mod folder.</summary>
internal static class PerkData
{
    // Null entries are cached on purpose: a character with no tree file would otherwise hit the disk on every
    // hook call, and hooks fire constantly.
    private static readonly Dictionary<string, TreeGraph?> Graphs = new(StringComparer.OrdinalIgnoreCase);
    private static EffectCatalog? _catalog;

    private static string DataDir =>
        Path.Combine(Path.GetDirectoryName(typeof(PerkData).Assembly.Location) ?? ".", "data");

    /// <summary>A fresh state for this player, or one restored from <paramref name="saved"/> when loading a run.</summary>
    public static PerkState? CreateState(Player player, PerkPlayerSave? saved = null)
    {
        var characterId = player.Character.Id.Entry;
        var graph = GraphFor(characterId);
        if (graph is null) return null;
        if (saved is null) return new PerkState(graph);

        var state = PerkState.FromSave(graph, saved.State, out var report);
        MainFile.Log($"restored perks for {characterId}: {state.Committed.Count} committed, {state.Pending.Count} pending, {state.UnspentPoints} unspent");
        if (report.DroppedNodes.Count > 0)
            // The tree data changed since the save; the points were refunded (see PerkState.FromSave).
            MainFile.LogWarn($"{characterId}: dropped {string.Join(", ", report.DroppedNodes)} and refunded {report.RefundedPoints} point(s)");
        else if (saved.CommittedHash != 0 && saved.CommittedHash != state.CommittedHash())
            MainFile.LogWarn($"{characterId}: restored perks do not match the hash that was saved");
        return state;
    }

    /// <summary>A chance other than 100% on an effect that cannot roll does nothing; say so once at load instead of letting a designer wonder.</summary>
    private static void WarnIgnoredChances(string characterId, TreeGraph graph)
    {
        var ignored = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
            if (Effects.HandlerRegistry.GuaranteedOnly.Contains(node.EffectId) && (node.Chance ?? graph.EffectOf(node).Chance) != 1m)
                ignored.Add(node.EffectId);
        if (ignored.Count > 0)
            MainFile.LogWarn($"{characterId}: chance is ignored (always 100%) on {string.Join(", ", ignored)}");
    }

    private static TreeGraph? GraphFor(string characterId)
    {
        if (Graphs.TryGetValue(characterId, out var cached)) return cached;

        TreeGraph? graph = null;
        try
        {
            var treePath = Path.Combine(DataDir, $"{characterId.ToLowerInvariant()}.tree.json");
            if (File.Exists(treePath))
            {
                _catalog ??= PerkJson.LoadCatalogFile(Path.Combine(DataDir, "effects.json"));
                // Keywords are not checked at runtime; `perktree validate --keywords` does that at build time.
                graph = TreeGraph.Build(PerkJson.LoadTreeFile(treePath), _catalog);
                MainFile.Log($"loaded tree for {characterId}: {graph.Tree.Nodes.Count} nodes");
                WarnIgnoredChances(characterId, graph);
            }
            else
            {
                MainFile.Log($"no tree file for {characterId}; perks disabled for that character");
            }
        }
        catch (Exception e)
        {
            // A broken data file must disable perks, not crash the run.
            MainFile.LogWarn($"failed to load tree for {characterId}: {e.Message}");
        }

        Graphs[characterId] = graph;
        return graph;
    }
}
