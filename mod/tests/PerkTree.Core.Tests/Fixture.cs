using PerkTree.Core;

namespace PerkTree.Core.Tests;

internal static class Fixture
{
    private static string SamplePath(string file) => Path.Combine(AppContext.BaseDirectory, "sample", file);

    public static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "Poison", "Thorns", "Vulnerable", "Weak", "Frail" };

    // Fresh copy every call: several tests mutate the lists to build a broken variant.
    public static TreeDef Tree() => PerkJson.LoadTreeFile(SamplePath("tree.sample.json"));
    public static EffectCatalog Catalog() => PerkJson.LoadCatalogFile(SamplePath("effects.sample.json"));
    public static string TreeText() => File.ReadAllText(SamplePath("tree.sample.json"));

    public static TreeGraph Graph() => TreeGraph.Build(Tree(), Catalog(), Keywords);

    /// <summary>A state with the window open and some points to spend.</summary>
    public static PerkState OpenState(int points, TreeGraph? graph = null)
    {
        var s = new PerkState(graph ?? Graph());
        s.AwardPoints(points);
        s.OpenAllocationWindow();
        return s;
    }

    public static NodeDef Node(string id, string effect, decimal magnitude, string branch = "util", decimal? chance = null) =>
        new() { Id = id, Branch = branch, Tier = NodeTier.Small, EffectId = effect, Magnitude = magnitude, Chance = chance };
}
