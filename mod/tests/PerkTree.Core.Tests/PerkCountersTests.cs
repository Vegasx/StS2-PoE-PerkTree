using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class PerkCountersTests
{
    [Fact]
    public void Counters_start_at_zero_and_accumulate()
    {
        var c = new PerkCounters();
        Assert.Equal(0, c.Get("a"));
        Assert.Equal(1, c.Add("a"));
        Assert.Equal(4, c.Add("a", 3));
        Assert.Equal(0, c.Get("b"));
    }

    [Fact]
    public void Hash_ignores_insertion_order_and_zero_counters()
    {
        var one = new PerkCounters();
        one.Add("a", 2); one.Add("b", 5);
        var two = new PerkCounters();
        two.Add("b", 5); two.Add("never_used", 1); two.Add("never_used", -1); two.Add("a", 2);
        Assert.Equal(one.Hash(), two.Hash());
    }

    [Fact]
    public void Hash_changes_with_any_value_or_key()
    {
        var baseline = new PerkCounters();
        baseline.Add("attacks", 11);
        var off_by_one = new PerkCounters();
        off_by_one.Add("attacks", 12);
        var other_key = new PerkCounters();
        other_key.Add("turns", 11);
        Assert.NotEqual(baseline.Hash(), off_by_one.Hash());
        Assert.NotEqual(baseline.Hash(), other_key.Hash());
    }

    [Fact]
    public void Counters_survive_the_sidecar_round_trip()
    {
        var s = Fixture.OpenState(3);
        s.TryAllocate("util_1");
        s.Commit();
        s.Counters.Add("energy_per_attacks.attacks", 37);
        s.Counters.Add("energy_every_n_turns.turns", 9);

        var save = PerkRunSave.Capture(1L, new[] { (7UL, "SILENT", s) });
        Assert.True(PerkRunSave.TryParse(save.ToJson(), out var loaded));
        var restored = PerkState.FromSave(Fixture.Graph(), loaded!.For(7UL, "SILENT")!.State, out _);

        Assert.Equal(37, restored.Counters.Get("energy_per_attacks.attacks"));
        Assert.Equal(9, restored.Counters.Get("energy_every_n_turns.turns"));
        Assert.Equal(s.SyncHash(), restored.SyncHash());
        Assert.Equal(s.SyncHash(), loaded.For(7UL, "SILENT")!.SyncHash);
    }

    [Fact]
    public void Sync_hash_catches_a_counter_that_drifted_even_when_the_committed_nodes_match()
    {
        var a = Fixture.OpenState(3);
        a.TryAllocate("util_1"); a.Commit();
        var b = Fixture.OpenState(3);
        b.TryAllocate("util_1"); b.Commit();
        Assert.Equal(a.SyncHash(), b.SyncHash());

        a.Counters.Add("energy_per_attacks.attacks");
        Assert.Equal(a.CommittedHash(), b.CommittedHash());   // same nodes...
        Assert.NotEqual(a.SyncHash(), b.SyncHash());          // ...but the peers have diverged
    }

    [Fact]
    public void A_save_from_before_counters_existed_loads_as_all_zero()
    {
        var s = Fixture.OpenState(3);
        s.TryAllocate("util_1"); s.Commit();
        var json = PerkRunSave.Capture(1L, new[] { (7UL, "SILENT", s) }).ToJson();
        // Strip the counters entry the way an older mod version's file would lack it.
        var old = System.Text.RegularExpressions.Regex.Replace(json, "\"counters\"\\s*:\\s*\\{[^}]*\\},?", "");
        Assert.False(old.Contains("counters"));

        Assert.True(PerkRunSave.TryParse(old, out var loaded));
        var restored = PerkState.FromSave(Fixture.Graph(), loaded!.For(7UL, "SILENT")!.State, out _);
        Assert.Equal(0, restored.Counters.Get("energy_per_attacks.attacks"));
    }

    [Fact]
    public void Negative_saved_counters_are_dropped_not_trusted()
    {
        var restored = PerkCounters.FromSave(new Dictionary<string, long> { ["a"] = -5, ["b"] = 3, [""] = 9 });
        Assert.Equal(0, restored.Get("a"));
        Assert.Equal(3, restored.Get("b"));
        Assert.Equal(0, restored.Get(""));
    }

    [Fact]
    public void Allocating_and_committing_never_touches_counters()
    {
        var s = Fixture.OpenState(5);
        s.Counters.Add("x", 4);
        s.TryAllocate("util_1");
        s.TryRefundPending("util_1");
        s.TryAllocate("util_1");
        s.Commit();
        Assert.Equal(4, s.Counters.Get("x"));
    }
}
