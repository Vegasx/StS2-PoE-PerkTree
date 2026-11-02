using System.Text;

namespace PerkTree.Core;

public enum Severity { Warning, Error }

public sealed record ValidationIssue(Severity Severity, string Code, string Message);

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = new();

    public bool IsValid => !Issues.Any(i => i.Severity == Severity.Error);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == Severity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == Severity.Warning);

    internal void Error(string code, string message) => Issues.Add(new(Severity.Error, code, message));
    internal void Warn(string code, string message) => Issues.Add(new(Severity.Warning, code, message));

    public bool Has(string code) => Issues.Any(i => i.Code == code);

    public override string ToString()
    {
        if (Issues.Count == 0) return "OK";
        var sb = new StringBuilder();
        foreach (var i in Issues) sb.AppendLine($"{i.Severity.ToString().ToUpperInvariant(),-7} {i.Code}: {i.Message}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The build/dev-time validator from design doc section 10. Errors mean the data cannot be used;
/// warnings mean it works but probably isn't what the author intended.
/// </summary>
public static class TreeValidator
{
    /// <param name="knownKeywords">
    /// Every keyword/power id that resolves (native game ones plus mod-registered ones). Null skips the
    /// keyword check, because the native list can only be produced from inside the game (UI recon, step 2).
    /// </param>
    public static ValidationReport Validate(TreeDef tree, EffectCatalog catalog, ISet<string>? knownKeywords = null)
    {
        var r = new ValidationReport();

        if (tree.SchemaVersion != SchemaVersions.Current)
            r.Error("SCHEMA_VERSION", $"Tree schemaVersion {tree.SchemaVersion} is not supported (expected {SchemaVersions.Current}).");
        if (string.IsNullOrWhiteSpace(tree.CharacterId))
            r.Error("TREE_NO_CHARACTER", "Tree has no characterId.");

        var effects = CheckCatalog(r, catalog, knownKeywords);

        // ---- branches ----
        var branches = new Dictionary<string, BranchDef>(StringComparer.Ordinal);
        foreach (var b in tree.Branches)
            if (string.IsNullOrWhiteSpace(b.Id)) r.Error("BRANCH_NO_ID", "A branch has no id.");
            else if (!branches.TryAdd(b.Id, b)) r.Error("BRANCH_DUPLICATE", $"Branch id '{b.Id}' is defined more than once.");

        // Design doc section 4: 1-3 character branches (one per unique effect archetype).
        int characterBranches = branches.Values.Count(b => b.Kind == BranchKind.Character);
        if (characterBranches is < 1 or > 3)
            r.Warn("BRANCH_COUNT", $"Tree has {characterBranches} character branches; the design calls for 1-3.");

        // ---- nodes ----
        var nodes = new Dictionary<string, NodeDef>(StringComparer.Ordinal);
        foreach (var n in tree.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) { r.Error("NODE_NO_ID", "A node has no id."); continue; }
            if (!nodes.TryAdd(n.Id, n)) { r.Error("NODE_DUPLICATE", $"Node id '{n.Id}' is defined more than once."); continue; }

            if (!branches.ContainsKey(n.Branch))
                r.Error("NODE_BRANCH_UNKNOWN", $"Node '{n.Id}' is in unknown branch '{n.Branch}'.");
            if (n.Magnitude == 0)
                r.Error("NODE_ZERO_MAGNITUDE", $"Node '{n.Id}' has magnitude 0, so it would do nothing.");
            if (n.Chance is { } nc && !ChanceMath.IsValidChance(nc))
                r.Error("NODE_BAD_CHANCE", $"Node '{n.Id}' has chance {nc}; it must be above 0 and at most {ChanceMath.MaxGuaranteed} (100 = 10000%).");

            if (!effects.TryGetValue(n.EffectId, out var effect))
            {
                r.Error("NODE_EFFECT_UNKNOWN", $"Node '{n.Id}' uses unknown effect '{n.EffectId}'.");
            }
            else if (effect.Characters.Count > 0 &&
                     !effect.Characters.Contains(tree.CharacterId, StringComparer.OrdinalIgnoreCase))
            {
                // A warning, not an error: putting another character's perk in a tree is allowed when it is done on purpose.
                // It can still be dead weight (a Shiv perk for a character with no Shiv cards), which is why it is flagged at all.
                r.Warn("NODE_EFFECT_WRONG_CHARACTER",
                    $"Node '{n.Id}' uses effect '{effect.Id}', which is meant for [{string.Join(", ", effect.Characters)}], not '{tree.CharacterId}'. It works only if this character can trigger it.");
            }
        }

        // ---- starts ----
        var starts = new HashSet<string>(StringComparer.Ordinal);
        if (tree.Starts.Count == 0) r.Error("NO_START", "Tree has no start nodes, so nothing could ever be allocated.");
        foreach (var s in tree.Starts)
            if (!nodes.ContainsKey(s)) r.Error("START_UNKNOWN", $"Start node '{s}' does not exist.");
            else starts.Add(s);

        // ---- edges ----
        var adjacency = nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var seenEdges = new HashSet<(string, string)>();
        foreach (var edge in tree.Edges)
        {
            if (edge is null || edge.Length != 2) { r.Error("EDGE_MALFORMED", "An edge must be exactly two node ids."); continue; }
            var (a, b) = (edge[0], edge[1]);
            if (a == b) { r.Error("EDGE_SELF", $"Edge connects node '{a}' to itself."); continue; }
            if (!nodes.ContainsKey(a) || !nodes.ContainsKey(b))
            {
                r.Error("EDGE_UNKNOWN_NODE", $"Edge [{a}, {b}] references a node that does not exist.");
                continue;
            }
            // Order the pair so [a,b] and [b,a] are recognised as the same undirected edge.
            var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            if (!seenEdges.Add(key)) { r.Warn("EDGE_DUPLICATE", $"Edge [{a}, {b}] appears more than once."); continue; }
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        // ---- reachability: every node must be allocatable by some route from a start ----
        var reached = new HashSet<string>(starts, StringComparer.Ordinal);
        var queue = new Queue<string>(starts);
        while (queue.Count > 0)
            foreach (var next in adjacency[queue.Dequeue()])
                if (reached.Add(next)) queue.Enqueue(next);
        foreach (var id in nodes.Keys.Where(id => !reached.Contains(id)))
            r.Error("NODE_UNREACHABLE", $"Node '{id}' cannot be reached from any start node.");

        // ---- caps: flag effects whose whole-tree total exceeds their cap ----
        // Warning, not error: a player only has ~35-45 points, so they can never take the whole tree,
        // but a cap that the full tree overshoots by a lot usually means a magnitude typo.
        foreach (var group in nodes.Values.Where(n => effects.ContainsKey(n.EffectId)).GroupBy(n => n.EffectId))
        {
            var cap = effects[group.Key].Cap;
            if (cap is null) continue;
            var max = group.Where(n => n.Magnitude > 0).Sum(n => n.Magnitude);
            if (max > cap)
                r.Warn("CAP_EXCEEDED", $"Effect '{group.Key}': tree offers up to {max} across {group.Count()} nodes but caps at {cap}.");
        }

        return r;
    }

    /// <summary>
    /// Checks an effect catalog on its own (no tree needed): ids, scopes, caps and, when a keyword list is given,
    /// keyword resolution. Used by the audit workflow, where the catalog exists before any tree does.
    /// </summary>
    public static ValidationReport ValidateCatalog(EffectCatalog catalog, ISet<string>? knownKeywords = null)
    {
        var r = new ValidationReport();
        CheckCatalog(r, catalog, knownKeywords);
        return r;
    }

    private static Dictionary<string, EffectDef> CheckCatalog(ValidationReport r, EffectCatalog catalog, ISet<string>? knownKeywords)
    {
        if (catalog.SchemaVersion != SchemaVersions.Current)
            r.Error("SCHEMA_VERSION", $"Effect catalog schemaVersion {catalog.SchemaVersion} is not supported (expected {SchemaVersions.Current}).");

        var effects = new Dictionary<string, EffectDef>(StringComparer.Ordinal);
        foreach (var e in catalog.Effects)
        {
            if (string.IsNullOrWhiteSpace(e.Id)) { r.Error("EFFECT_NO_ID", "An effect has no id."); continue; }
            if (!effects.TryAdd(e.Id, e)) { r.Error("EFFECT_DUPLICATE", $"Effect id '{e.Id}' is defined more than once."); continue; }
            if (e.Scope is null)
                r.Error("EFFECT_NO_SCOPE", $"Effect '{e.Id}' has no scope; classify it (self / enemyThroughMe / sharedEnemy).");
            // A cap of 0 or less with positive magnitudes would make every node using it worthless.
            if (e.Cap is <= 0)
                r.Error("EFFECT_BAD_CAP", $"Effect '{e.Id}' has cap {e.Cap}; a cap must be greater than zero.");
            if (!ChanceMath.IsValidChance(e.Chance))
                r.Error("EFFECT_BAD_CHANCE", $"Effect '{e.Id}' has chance {e.Chance}; it must be above 0 and at most {ChanceMath.MaxGuaranteed}.");
            if (knownKeywords != null)
                foreach (var kw in e.Keywords.Where(k => !knownKeywords.Contains(k)))
                    r.Error("EFFECT_KEYWORD_UNKNOWN", $"Effect '{e.Id}' references unknown keyword '{kw}'.");
        }

        return effects;
    }
}
