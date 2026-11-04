namespace PerkTree.Core;

public static class SchemaVersions
{
    /// <summary>
    /// 2: a node's magnitude is always the AMOUNT it gives, and the chance to get it is a separate field (effect default,
    /// optional per-node override). In 1 a few effects stored their chance as the magnitude.
    /// </summary>
    public const int Current = 2;
}

/// <summary>
/// Who an effect touches (design doc section 3). Every effect must be classified explicitly; the
/// validator rejects an effect with no scope, because a silently-defaulted "Self" on what is really a
/// shared enemy debuff would change what teammates fight without anyone noticing.
/// </summary>
public enum EffectScope
{
    /// <summary>Affects only the owning player.</summary>
    Self,
    /// <summary>Changes enemies only in their interactions with the owner.</summary>
    EnemyThroughMe,
    /// <summary>Changes the enemy for ALL players (Vulnerable/Weak-style). Must be labeled in tooltips.</summary>
    SharedEnemy,
}

public enum NodeTier { Small, Notable, Keystone }

public enum BranchKind { Character, General }

public enum RoomKind { Normal, Elite, Boss }

/// <summary>One prefix effect. Shared across characters, so it lives in the catalog, not in a tree.</summary>
public sealed class EffectDef
{
    public string Id { get; init; } = "";

    /// <summary>Nullable only so a missing value can be detected and reported; see <see cref="EffectScope"/>.</summary>
    public EffectScope? Scope { get; init; }

    /// <summary>
    /// Natural limit of the summed effect (an on/off switch at 1, a resistance at the vanilla size...). Null = uncapped. Chance
    /// effects are NOT capped at 100%: past it they grant extra guaranteed procs (see <see cref="ChanceMath.Split"/>).
    /// </summary>
    public decimal? Cap { get; init; }

    /// <summary>
    /// Game keywords/powers this effect's text mentions (Thorns, Vulnerable...). Companion tooltips are
    /// generated from this list, so nothing needs per-perk tooltip authoring.
    /// </summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>
    /// Default chance that one of this effect's nodes gives its amount when the effect triggers (1 = always, 0.5 = half the time).
    /// A node can override it. Past 1 the whole part is guaranteed and the rest is rolled, so 1.5 is a sure proc plus a 50% one.
    /// The mod ignores a chance below 1 on the few effects that cannot roll (see HandlerRegistry.GuaranteedOnly) and says so in the log.
    /// </summary>
    public decimal Chance { get; init; } = 1m;

    /// <summary>If non-empty, the characters this effect is meant for. Using it in another character's tree is allowed but warned about, since it may do nothing there (a Shiv perk without Shiv cards).</summary>
    public List<string> Characters { get; init; } = new();
}

public sealed class EffectCatalog
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public List<EffectDef> Effects { get; init; } = new();
}

public sealed class BranchDef
{
    public string Id { get; init; } = "";

    /// <summary>An internal label for data and tooling. The tree UI does not show branch titles.</summary>
    public string Name { get; init; } = "";
    public BranchKind Kind { get; init; }
}

public sealed class NodeDef
{
    public string Id { get; init; } = "";
    public string Branch { get; init; } = "";
    public NodeTier Tier { get; init; }
    public string EffectId { get; init; } = "";

    /// <summary>Amount this node gives each time it succeeds. Decimal so "three +10% perks" is exactly 30%, not 30.000000000000004%.</summary>
    public decimal Magnitude { get; init; }

    /// <summary>Chance for this node to give its amount, overriding the effect's default. Null = use the effect's.</summary>
    public decimal? Chance { get; init; }

    // Layout only; Core never reads these. They live here so the tree stays one data-driven file.
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>The editor colour of the node ("#rrggbb"), for the in-game tree. Null means the default grey.</summary>
    public string? Color { get; init; }
}

public sealed class TreeDef
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public string CharacterId { get; init; } = "";

    /// <summary>Entry nodes: the only nodes that can be allocated without an allocated neighbor.</summary>
    public List<string> Starts { get; init; } = new();
    public List<BranchDef> Branches { get; init; } = new();
    public List<NodeDef> Nodes { get; init; } = new();

    /// <summary>Undirected links, each a two-element [idA, idB] array.</summary>
    public List<string[]> Edges { get; init; } = new();

    // Display only, for the in-game tree: the editor's sockets (free joints the rules skip) and every line as drawn in the editor,
    // arcs already flattened to points by the exporter. Core never reads these; older tree files simply have none.
    public List<SocketDef> Sockets { get; init; } = new();
    public List<LineDef> Lines { get; init; } = new();
}

public sealed class SocketDef
{
    public string Id { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

public sealed class LineDef
{
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    /// <summary>Points along an arc, start to end. Null for a straight line.</summary>
    public List<double[]>? Pts { get; init; }
}
