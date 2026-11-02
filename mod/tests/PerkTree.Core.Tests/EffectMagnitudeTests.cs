using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class EffectMagnitudeTests
{
    /// <summary>
    /// A tiny tree where the node you must take FIRST ("z_start") sorts LAST by id, so allocation order and id order disagree.
    /// Both nodes use the same effect with different magnitudes.
    /// </summary>
    private static TreeGraph TwoNodeGraph()
    {
        var tree = Fixture.Tree();
        tree.Starts.Add("z_start");
        tree.Nodes.Add(Fixture.Node("z_start", "sample.discard_chance", 0.5m));
        tree.Nodes.Add(Fixture.Node("a_next", "sample.discard_chance", 0.25m));
        tree.Edges.Add(new[] { "z_start", "a_next" });
        return TreeGraph.Build(tree, Fixture.Catalog(), Fixture.Keywords);
    }

    [Fact]
    public void Magnitudes_are_listed_in_node_id_order_not_allocation_order()
    {
        var s = new PerkState(TwoNodeGraph());
        s.AwardPoints(2);
        s.OpenAllocationWindow();
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("z_start"));   // must come first to reach a_next
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("a_next"));
        s.Commit();

        var total = s.GetTotal("sample.discard_chance");
        Assert.Equal(new[] { 0.25m, 0.5m }, total.NodeRolls.Select(r => r.Amount).ToArray());   // a_next, then z_start
        Assert.Equal(0.75m, total.Raw);
    }

    [Fact]
    public void A_state_restored_from_a_save_lists_the_same_order_as_the_original()
    {
        var graph = TwoNodeGraph();
        var original = new PerkState(graph);
        original.AwardPoints(2);
        original.TryCommitExact(new[] { "z_start", "a_next" });

        var restored = PerkState.FromSave(graph, original.ToSave(), out _);
        Assert.Equal(original.GetTotal("sample.discard_chance").NodeRolls,
                     restored.GetTotal("sample.discard_chance").NodeRolls);
    }

    [Fact]
    public void An_effect_with_no_nodes_has_no_magnitudes()
        => Assert.Empty(Fixture.OpenState(1).GetTotal("sample.discard_chance").NodeRolls);

    // ---- chance: effect default, per-node override --------------------------------------------------------------

    private static PerkState StateWith(EffectDef effect, params (string Id, decimal Amount, decimal? Chance)[] nodes)
    {
        var tree = Fixture.Tree();
        tree.Starts.Add(nodes[0].Id);
        foreach (var n in nodes) tree.Nodes.Add(Fixture.Node(n.Id, effect.Id, n.Amount, chance: n.Chance));
        for (var i = 1; i < nodes.Length; i++) tree.Edges.Add(new[] { nodes[i - 1].Id, nodes[i].Id });
        var catalog = Fixture.Catalog();
        catalog.Effects.RemoveAll(e => e.Id == effect.Id);
        catalog.Effects.Add(effect);
        var s = new PerkState(TreeGraph.Build(tree, catalog, Fixture.Keywords));
        s.AwardPoints(nodes.Length);
        s.TryCommitExact(nodes.Select(n => n.Id).ToArray());
        return s;
    }

    [Fact]
    public void A_node_without_a_chance_always_succeeds()
    {
        var s = StateWith(new EffectDef { Id = "sample.discard_chance", Scope = EffectScope.Self }, ("a", 2m, null));
        Assert.Equal(new[] { new NodeRoll(2m, 1m) }, s.GetTotal("sample.discard_chance").NodeRolls);
    }

    [Fact]
    public void A_node_uses_the_effects_default_chance_unless_it_has_its_own()
    {
        var effect = new EffectDef { Id = "sample.discard_chance", Scope = EffectScope.Self, Chance = 0.5m };
        var s = StateWith(effect, ("a", 1m, null), ("b", 1m, 0.75m));
        Assert.Equal(new[] { new NodeRoll(1m, 0.5m), new NodeRoll(1m, 0.75m) }, s.GetTotal("sample.discard_chance").NodeRolls);
    }

    [Fact]
    public void The_summed_amount_ignores_chance_because_the_cap_and_the_active_check_use_it()
    {
        var effect = new EffectDef { Id = "sample.discard_chance", Scope = EffectScope.Self, Chance = 0.1m };
        Assert.Equal(3m, StateWith(effect, ("a", 1m, null), ("b", 2m, null)).GetTotal("sample.discard_chance").Raw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(100.5)]
    public void A_chance_outside_zero_to_one_hundred_is_rejected_on_effects_and_nodes(double chance)
    {
        var bad = (decimal)chance;
        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "sample.bad", Scope = EffectScope.Self, Chance = bad });
        Assert.True(TreeValidator.ValidateCatalog(catalog).Has("EFFECT_BAD_CHANCE"));

        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("chancy", "sample.discard_chance", 1m, chance: bad));
        Assert.True(TreeValidator.Validate(tree, Fixture.Catalog(), Fixture.Keywords).Has("NODE_BAD_CHANCE"));
    }

    [Fact]
    public void A_chance_past_one_hundred_percent_is_allowed_because_it_stacks()
    {
        var catalog = Fixture.Catalog();
        catalog.Effects.Add(new EffectDef { Id = "sample.lucky", Scope = EffectScope.Self, Chance = 2.5m });
        Assert.False(TreeValidator.ValidateCatalog(catalog).Has("EFFECT_BAD_CHANCE"));
    }
}
