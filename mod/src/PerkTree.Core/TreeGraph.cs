using System.Diagnostics.CodeAnalysis;

namespace PerkTree.Core;

public sealed class InvalidTreeException : Exception
{
    public ValidationReport Report { get; }

    public InvalidTreeException(ValidationReport report)
        : base("Perk tree data is invalid:\n" + report) => Report = report;
}

/// <summary>
/// A validated, indexed, read-only view of one character's tree plus the effect catalog. Everything
/// that needs to ask "what is next to what" or "what does this node do" goes through here, so the
/// raw TreeDef lists are never searched linearly at runtime.
/// </summary>
public sealed class TreeGraph
{
    private readonly Dictionary<string, NodeDef> _nodes;
    private readonly Dictionary<string, EffectDef> _effects;
    private readonly Dictionary<string, List<string>> _adjacency;
    private readonly HashSet<string> _starts;

    public TreeDef Tree { get; }
    public EffectCatalog Catalog { get; }

    /// <summary>Warnings from validation (errors would have thrown).</summary>
    public ValidationReport Report { get; }

    private TreeGraph(TreeDef tree, EffectCatalog catalog, ValidationReport report)
    {
        Tree = tree;
        Catalog = catalog;
        Report = report;
        _nodes = tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _effects = catalog.Effects.ToDictionary(e => e.Id, StringComparer.Ordinal);
        _starts = new HashSet<string>(tree.Starts, StringComparer.Ordinal);
        _adjacency = _nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        // Duplicates were only a warning, so de-duplicate here rather than trusting the data.
        foreach (var e in tree.Edges)
        {
            if (!_adjacency[e[0]].Contains(e[1])) _adjacency[e[0]].Add(e[1]);
            if (!_adjacency[e[1]].Contains(e[0])) _adjacency[e[1]].Add(e[0]);
        }
    }

    /// <summary>Validates, then builds. Throws <see cref="InvalidTreeException"/> if there are errors.</summary>
    public static TreeGraph Build(TreeDef tree, EffectCatalog catalog, ISet<string>? knownKeywords = null)
    {
        var report = TreeValidator.Validate(tree, catalog, knownKeywords);
        if (!report.IsValid) throw new InvalidTreeException(report);
        return new TreeGraph(tree, catalog, report);
    }

    public string CharacterId => Tree.CharacterId;
    public IEnumerable<NodeDef> Nodes => Tree.Nodes;
    public IReadOnlySet<string> Starts => _starts;

    public bool TryGetNode(string id, [NotNullWhen(true)] out NodeDef? node) => _nodes.TryGetValue(id, out node);
    public NodeDef Node(string id) => _nodes[id];
    public EffectDef Effect(string id) => _effects[id];
    public EffectDef EffectOf(NodeDef node) => _effects[node.EffectId];
    public IReadOnlyList<string> Neighbors(string id) => _adjacency[id];
    public bool IsStart(string id) => _starts.Contains(id);

    /// <summary>
    /// Of the given allocated nodes, the ones still linked back to an allocated start node through
    /// allocated nodes only. A valid allocation set is one where this returns everything; it is how we
    /// decide whether refunding or loading would strand a node.
    /// </summary>
    public HashSet<string> ReachableFromStarts(IEnumerable<string> allocated)
    {
        var set = new HashSet<string>(allocated, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var id in set)
            if (_starts.Contains(id) && seen.Add(id)) queue.Enqueue(id);
        while (queue.Count > 0)
            foreach (var next in _adjacency[queue.Dequeue()])
                if (set.Contains(next) && seen.Add(next)) queue.Enqueue(next);
        return seen;
    }
}
