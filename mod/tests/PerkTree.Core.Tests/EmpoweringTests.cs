using PerkTree.Core;
using System.Text.Json;
using Xunit;

namespace PerkTree.Core.Tests;

public class EmpoweringTests
{
    // Read the editable definitions directly; exporting/syncing runtime files is the editor's job.
    // Make Regent's two Empowering nodes starts in this fixture so travel perks cannot alter the tested probability.
    private static TreeGraph Graph()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "editor");
        using var perks = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "perks.json")));
        var empowering = perks.RootElement.GetProperty("perks").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "proc_reroll");
        Assert.Equal("add", empowering.GetProperty("kind").GetString());
        var unit = empowering.GetProperty("unit").GetDecimal();
        var cap = empowering.GetProperty("cap").GetDecimal();
        var catalog = new EffectCatalog { Effects = [new EffectDef { Id = "proc_reroll", Scope = EffectScope.Self, Cap = cap }] };
        using var regent = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "regent.json")));
        var nodes = regent.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(n => n.TryGetProperty("fx", out var fx) && fx.GetString() == "proc_reroll")
            .Select(n => new NodeDef { Id = n.GetProperty("id").GetString()!, Branch = "general", EffectId = "proc_reroll",
                Magnitude = n.TryGetProperty("amount", out var amount) ? amount.GetDecimal() : unit }).ToList();
        Assert.Equal(2, nodes.Count);
        return TreeGraph.Build(new TreeDef
        {
            CharacterId = "REGENT",
            Branches = [new BranchDef { Id = "general", Kind = BranchKind.General }],
            Nodes = nodes,
            Starts = nodes.Select(n => n.Id).ToList(),
        }, catalog);
    }

    [Fact]
    public void Second_node_increases_the_committed_total_and_survives_save_restore()
    {
        var graph = Graph();
        var ids = graph.Tree.Starts;
        var state = Fixture.OpenState(2, graph);
        Assert.Equal(PerkOpResult.Ok, state.TryAllocate(ids[0]));
        state.Commit();
        Assert.Equal(1m, state.GetTotal("proc_reroll").Effective);
        state.OpenAllocationWindow();
        Assert.Equal(PerkOpResult.Ok, state.TryAllocate(ids[1]));
        Assert.Equal(1m, state.GetTotal("proc_reroll").Effective);
        Assert.Equal(2m, state.GetTotal("proc_reroll", includePending: true).Effective);
        state.Commit();
        var restored = PerkState.FromSave(graph, state.ToSave(), out var report);
        Assert.Empty(report.DroppedNodes);
        Assert.Equal(2m, restored.GetTotal("proc_reroll").Effective);
        Assert.Equal(3, ModifierMath.ProcRolls(restored.GetTotal("proc_reroll").Effective));
    }

    [Theory]
    [InlineData(1, 2, 0)]
    [InlineData(2, 3, 1)]
    public void Only_two_nodes_can_reach_a_success_on_the_third_attempt(int nodes, int expectedRolls, int expectedAmount)
    {
        var graph = Graph();
        var state = Fixture.OpenState(2, graph);
        foreach (var id in graph.Tree.Starts.Take(nodes)) Assert.Equal(PerkOpResult.Ok, state.TryAllocate(id));
        state.Commit();
        var rolls = 0;
        var tries = ModifierMath.ProcRolls(state.GetTotal("proc_reroll").Effective);
        var amount = ChanceMath.RollTotal([new NodeRoll(1m, 0.5m)], 0, 0, tries, _ => ++rolls == 3);
        Assert.Equal(expectedRolls, rolls);
        Assert.Equal(expectedAmount, amount);
        var preview = ProcChancePreview.Calculate(0.5m, 0, 0, state.GetTotal("proc_reroll").Effective);
        Assert.Equal(expectedRolls, preview.Rolls);
        Assert.Equal(nodes == 1 ? 0.75m : 0.875m, preview.AtLeastOneChance);
    }
}
