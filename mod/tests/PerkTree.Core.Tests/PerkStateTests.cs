using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class PerkStateTests
{
    // ------------------------------------------------------------ points

    [Theory]
    [InlineData(RoomKind.Normal, 1)]
    [InlineData(RoomKind.Elite, 2)]
    [InlineData(RoomKind.Boss, 3)]
    public void Combat_awards_match_the_design(RoomKind kind, int expected)
    {
        var s = new PerkState(Fixture.Graph());
        s.AwardForCombat(kind);
        Assert.Equal(expected, s.UnspentPoints);
    }

    [Fact]
    public void Negative_award_is_rejected()
    {
        var s = new PerkState(Fixture.Graph());
        Assert.Throws<ArgumentOutOfRangeException>(() => s.AwardPoints(-1));
    }

    // ------------------------------------------------------------ allocation rules

    [Fact]
    public void Cannot_allocate_while_the_window_is_closed()
    {
        var s = new PerkState(Fixture.Graph());
        s.AwardPoints(5);
        Assert.Equal(PerkOpResult.WindowClosed, s.TryAllocate("poison_1"));
    }

    [Fact]
    public void Cannot_allocate_without_points()
    {
        var s = Fixture.OpenState(0);
        Assert.Equal(PerkOpResult.NoPoints, s.TryAllocate("poison_1"));
    }

    [Fact]
    public void Start_nodes_need_no_neighbor_but_other_nodes_do()
    {
        var s = Fixture.OpenState(5);
        Assert.Equal(PerkOpResult.NotConnected, s.TryAllocate("poison_2"));
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("poison_1"));
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("poison_2"));
        Assert.Equal(3, s.UnspentPoints);
    }

    [Fact]
    public void Every_start_node_can_be_taken_at_any_time_even_when_other_nodes_are_already_allocated()
    {
        var s = Fixture.OpenState(5);
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("poison_1"));
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("poison_2"));
        // util_1 is a second, unconnected entry point: no neighbour of it is allocated
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("util_1"));
        Assert.Equal(2, s.UnspentPoints);
    }

    [Fact]
    public void Refunding_a_start_is_blocked_only_while_a_node_still_depends_on_it()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("poison_1");
        s.TryAllocate("poison_2");
        s.TryAllocate("util_1");
        // util_1 is its own entry point and nothing hangs off it, so it can go
        Assert.Equal(PerkOpResult.Ok, s.TryRefundPending("util_1"));
        // poison_1 is the only start poison_2 touches, so it has to stay
        Assert.Equal(PerkOpResult.WouldDisconnect, s.TryRefundPending("poison_1"));
    }

    [Fact]
    public void A_node_adjacent_to_any_allocated_node_is_reachable_even_from_another_branch()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("poison_1");
        s.TryAllocate("poison_2");
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("gen_1")); // joins the poison branch to the general one
    }

    [Fact]
    public void Unknown_and_duplicate_allocations_are_refused_without_spending()
    {
        var s = Fixture.OpenState(5);
        Assert.Equal(PerkOpResult.UnknownNode, s.TryAllocate("nope"));
        s.TryAllocate("poison_1");
        Assert.Equal(PerkOpResult.AlreadyAllocated, s.TryAllocate("poison_1"));
        Assert.Equal(4, s.UnspentPoints);
    }

    // ------------------------------------------------------------ pending / refund / commit

    [Fact]
    public void Pending_nodes_do_not_count_toward_gameplay_totals_until_committed()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("util_1");

        Assert.Equal(0m, s.GetTotal("sample.discard_chance").Effective);
        Assert.Equal(0.25m, s.GetTotal("sample.discard_chance", includePending: true).Effective);

        s.Commit();
        Assert.Equal(0.25m, s.GetTotal("sample.discard_chance").Effective);
    }

    [Fact]
    public void Refunding_a_pending_leaf_returns_the_point()
    {
        var s = Fixture.OpenState(3);
        s.TryAllocate("util_1");
        s.TryAllocate("util_2");

        Assert.Equal(PerkOpResult.Ok, s.TryRefundPending("util_2"));
        Assert.Equal(2, s.UnspentPoints);
        Assert.DoesNotContain("util_2", s.Pending);
    }

    [Fact]
    public void Refund_that_would_strand_another_pending_node_is_refused()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("util_1");
        s.TryAllocate("util_2");
        s.TryAllocate("util_3");

        Assert.Equal(PerkOpResult.WouldDisconnect, s.TryRefundPending("util_2"));
        Assert.Equal(PerkOpResult.WouldDisconnect, s.TryRefundPending("util_1"));
        Assert.Equal(PerkOpResult.Ok, s.TryRefundPending("util_3"));
        Assert.Equal(PerkOpResult.Ok, s.TryRefundPending("util_2"));
        Assert.Equal(PerkOpResult.Ok, s.TryRefundPending("util_1"));
        Assert.Equal(5, s.UnspentPoints);
    }

    [Fact]
    public void Refund_is_allowed_when_a_second_route_keeps_the_node_connected()
    {
        // gen_1 touches both poison_2 and util_2. With both branches allocated, either can go.
        var s = Fixture.OpenState(9);
        foreach (var id in new[] { "poison_1", "poison_2", "util_1", "util_2", "gen_1" })
            Assert.Equal(PerkOpResult.Ok, s.TryAllocate(id));

        Assert.Equal(PerkOpResult.Ok, s.TryRefundPending("poison_2")); // gen_1 still hangs off util_2
    }

    [Fact]
    public void Committed_nodes_cannot_be_refunded()
    {
        var s = Fixture.OpenState(3);
        s.TryAllocate("util_1");
        s.Commit();
        s.OpenAllocationWindow();

        Assert.Equal(PerkOpResult.NotPending, s.TryRefundPending("util_1"));
        Assert.Contains("util_1", s.Committed);
    }

    [Fact]
    public void Commit_locks_the_player_until_the_window_reopens()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("util_1");
        Assert.Equal(1, s.Commit());

        Assert.False(s.AllocationOpen);
        Assert.Empty(s.Pending);
        Assert.Equal(PerkOpResult.WindowClosed, s.TryAllocate("util_2"));
        Assert.Equal(PerkOpResult.WindowClosed, s.TryRefundPending("util_1"));

        s.OpenAllocationWindow(); // next map screen
        Assert.Equal(PerkOpResult.Ok, s.TryAllocate("util_2"));
    }

    [Fact]
    public void Voting_with_nothing_pending_still_locks_and_keeps_unspent_points()
    {
        var s = Fixture.OpenState(2);
        Assert.Equal(0, s.Commit());
        Assert.False(s.AllocationOpen);
        Assert.Equal(2, s.UnspentPoints); // missed the window, not lost: carries to the next map screen
    }

    [Fact]
    public void RefundAllPending_returns_everything()
    {
        var s = Fixture.OpenState(4);
        s.TryAllocate("util_1");
        s.TryAllocate("util_2");
        Assert.Equal(2, s.RefundAllPending());
        Assert.Equal(4, s.UnspentPoints);
        Assert.Empty(s.Pending);
    }

    // ------------------------------------------------------------ totals & caps

    [Fact]
    public void Effects_stack_linearly_and_report_node_count()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("poison_1");
        s.TryAllocate("poison_2");
        s.TryAllocate("poison_3");
        s.Commit();

        var t = s.GetTotal("sample.poison_bonus");
        Assert.Equal(1.0m, t.Raw); // 0.25 + 0.25 + 0.50
        Assert.Equal(3, t.NodeCount);
        Assert.Null(t.Cap);
        Assert.False(t.AtCap);
    }

    [Fact]
    public void Totals_clamp_at_the_cap_and_report_the_waste()
    {
        var tree = Fixture.Tree();
        tree.Nodes.Add(Fixture.Node("util_4", "sample.discard_chance", 0.25m));
        tree.Nodes.Add(Fixture.Node("util_5", "sample.discard_chance", 0.25m));
        tree.Edges.Add(new[] { "util_3", "util_4" });
        tree.Edges.Add(new[] { "util_4", "util_5" });
        var s = Fixture.OpenState(9, TreeGraph.Build(tree, Fixture.Catalog(), Fixture.Keywords));

        foreach (var id in new[] { "util_1", "util_2", "util_3", "util_4", "util_5" }) s.TryAllocate(id);
        s.Commit();

        var t = s.GetTotal("sample.discard_chance");
        Assert.Equal(1.25m, t.Raw);
        Assert.Equal(1.0m, t.Effective);
        Assert.True(t.AtCap);
        Assert.Equal(0.25m, t.Wasted);
    }

    [Fact]
    public void Reaching_the_cap_exactly_flags_AtCap_with_no_waste()
    {
        var t = new EffectTotal("x", 1.0m, 1.0m, 4);
        Assert.True(t.AtCap);
        Assert.Equal(0m, t.Wasted);
    }

    [Fact]
    public void Decimal_stacking_has_no_float_drift()
    {
        var t = new EffectTotal("x", 0.1m + 0.1m + 0.1m, null, 3);
        Assert.Equal(0.3m, t.Effective);
    }

    [Fact]
    public void Untouched_effect_reports_zero_with_its_cap()
    {
        var s = Fixture.OpenState(1);
        var t = s.GetTotal("sample.discard_chance");
        Assert.Equal(0m, t.Raw);
        Assert.Equal(1.0m, t.Cap);
        Assert.Equal(0, t.NodeCount);
    }

    [Fact]
    public void Preview_shows_before_and_after_for_a_hover_tooltip()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("util_1");

        var (before, after) = s.PreviewAllocate("util_2");
        Assert.Equal(0.25m, before.Raw);
        Assert.Equal(0.50m, after.Raw);

        var (b2, a2) = s.PreviewAllocate("util_1"); // already taken: no change
        Assert.Equal(b2, a2);
    }

    // ------------------------------------------------------------ save / load

    [Fact]
    public void Save_round_trips_committed_pending_and_unspent_without_committing_pending()
    {
        var s = Fixture.OpenState(6);
        s.TryAllocate("poison_1");
        s.TryAllocate("poison_2");
        s.Commit();
        s.OpenAllocationWindow();
        s.TryAllocate("poison_3");

        var json = PerkJson.Serialize(s.ToSave());
        var loaded = PerkState.FromSave(Fixture.Graph(), PerkJson.Deserialize<PerkSaveData>(json)!, out var report);

        Assert.Empty(report.DroppedNodes);
        Assert.Equal(new[] { "poison_1", "poison_2" }, loaded.Committed.OrderBy(x => x));
        Assert.Equal(new[] { "poison_3" }, loaded.Pending);
        Assert.Equal(s.UnspentPoints, loaded.UnspentPoints);
        Assert.False(loaded.AllocationOpen); // the host reopens it when the map screen is shown
    }

    [Fact]
    public void Load_drops_removed_nodes_and_refunds_a_point_each()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("poison_1");
        s.TryAllocate("poison_2");
        s.Commit();
        s.OpenAllocationWindow();
        s.TryAllocate("poison_3");
        var save = s.ToSave(); // unspent = 2

        var newer = Fixture.Tree();
        newer.Nodes.RemoveAll(n => n.Id == "poison_3");
        newer.Edges.RemoveAll(e => e.Contains("poison_3"));

        var loaded = PerkState.FromSave(TreeGraph.Build(newer, Fixture.Catalog(), Fixture.Keywords), save, out var report);

        Assert.Equal(new[] { "poison_3" }, report.DroppedNodes);
        Assert.Equal(1, report.RefundedPoints);
        Assert.Empty(loaded.Pending);
        Assert.Equal(3, loaded.UnspentPoints);
    }

    [Fact]
    public void Load_drops_nodes_that_a_tree_edit_cut_off_from_every_start()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("poison_1");
        s.TryAllocate("poison_2");
        s.TryAllocate("poison_3");
        var save = s.ToSave();

        // poison_3 is now only linked via gen_1, which this player never took.
        var newer = Fixture.Tree();
        newer.Edges.RemoveAll(e => e.Contains("poison_3"));
        newer.Edges.Add(new[] { "gen_1", "poison_3" });

        var loaded = PerkState.FromSave(TreeGraph.Build(newer, Fixture.Catalog(), Fixture.Keywords), save, out var report);

        Assert.Equal(new[] { "poison_3" }, report.DroppedNodes);
        Assert.Equal(new[] { "poison_1", "poison_2" }, loaded.Pending.OrderBy(x => x));
        Assert.Equal(3, loaded.UnspentPoints); // 2 unspent + 1 refund
    }

    [Fact]
    public void Load_tolerates_duplicate_and_overlapping_ids()
    {
        var save = new PerkSaveData
        {
            Committed = { "util_1", "util_1" },
            Pending = { "util_1", "util_2", "util_2" },
            Unspent = -4, // corrupt value must not give negative points
        };
        var loaded = PerkState.FromSave(Fixture.Graph(), save, out _);

        Assert.Equal(new[] { "util_1" }, loaded.Committed);
        Assert.Equal(new[] { "util_2" }, loaded.Pending);
        Assert.Equal(0, loaded.UnspentPoints);
    }

    // ------------------------------------------------------------ co-op hash

    [Fact]
    public void Committed_hash_ignores_allocation_order_but_detects_differences()
    {
        var a = Fixture.OpenState(5);
        a.TryAllocate("util_1"); a.TryAllocate("poison_1"); a.TryAllocate("poison_2"); a.Commit();

        var b = Fixture.OpenState(5);
        b.TryAllocate("poison_1"); b.TryAllocate("poison_2"); b.TryAllocate("util_1"); b.Commit();

        var c = Fixture.OpenState(5);
        c.TryAllocate("poison_1"); c.TryAllocate("poison_2"); c.Commit();

        Assert.Equal(a.CommittedHash(), b.CommittedHash());
        Assert.NotEqual(a.CommittedHash(), c.CommittedHash());
    }

    [Fact]
    public void Pending_nodes_do_not_affect_the_committed_hash()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("util_1");
        s.Commit();
        s.OpenAllocationWindow();
        var before = s.CommittedHash();
        s.TryAllocate("util_2");
        Assert.Equal(before, s.CommittedHash());
    }
}
