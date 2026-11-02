using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class PerkCommitTests
{
    // Sample tree: starts are poison_1 and util_1; poison_1-poison_2-gen_1, util_1-util_2-gen_1, gen_1-gen_2-gen_3...

    /// <summary>A player who picked nodes in the UI, as they exist on their OWN machine.</summary>
    private static PerkState Sender(int points, params string[] picks)
    {
        var s = Fixture.OpenState(points);
        foreach (var id in picks) Assert.Equal(PerkOpResult.Ok, s.TryAllocate(id));
        return s;
    }

    /// <summary>The same player as another client sees them: same points, nothing pending, window never opened.</summary>
    private static PerkState Remote(int points)
    {
        var s = new PerkState(Fixture.Graph());
        s.AwardPoints(points);
        return s;
    }

    [Fact]
    public void Sender_and_remote_end_in_exactly_the_same_state()
    {
        var sender = Sender(5, "poison_1", "poison_2", "util_1");
        var remote = Remote(5);
        var list = sender.Pending.ToList();

        Assert.Equal(PerkCommitResult.Ok, sender.TryCommitExact(list));
        Assert.Equal(PerkCommitResult.Ok, remote.TryCommitExact(list));

        Assert.Equal(sender.SyncHash(), remote.SyncHash());
        Assert.Equal(2, sender.UnspentPoints);
        Assert.Equal(sender.UnspentPoints, remote.UnspentPoints);
        Assert.Equal(sender.CommittedVersion, remote.CommittedVersion);
        Assert.Empty(sender.Pending);
        Assert.False(sender.AllocationOpen);
        Assert.False(remote.AllocationOpen);
    }

    [Fact]
    public void The_result_does_not_depend_on_whether_this_clients_window_was_open()
    {
        var open = Remote(3); open.OpenAllocationWindow();
        var closed = Remote(3);
        var list = new[] { "util_1", "util_2" };
        Assert.Equal(PerkCommitResult.Ok, open.TryCommitExact(list));
        Assert.Equal(PerkCommitResult.Ok, closed.TryCommitExact(list));
        Assert.Equal(open.SyncHash(), closed.SyncHash());
    }

    [Fact]
    public void An_empty_commit_is_fine_and_still_locks()
    {
        var s = Remote(2); s.OpenAllocationWindow();
        Assert.Equal(PerkCommitResult.Ok, s.TryCommitExact(Array.Empty<string>()));
        Assert.False(s.AllocationOpen);
        Assert.Equal(2, s.UnspentPoints);
        Assert.Equal(0, s.CommittedVersion);   // nothing changed, so cached totals stay valid
    }

    [Fact]
    public void Order_matters_a_node_must_touch_a_start_or_an_earlier_node()
    {
        Assert.Equal(PerkCommitResult.NotConnected, Remote(3).TryCommitExact(new[] { "util_2", "util_1" }));
        Assert.Equal(PerkCommitResult.Ok, Remote(3).TryCommitExact(new[] { "util_1", "util_2" }));
    }

    [Theory]
    [InlineData("nope")]
    public void Unknown_nodes_are_refused(string id)
        => Assert.Equal(PerkCommitResult.UnknownNode, Remote(3).TryCommitExact(new[] { id }));

    [Fact]
    public void Duplicates_already_committed_nodes_and_overspending_are_refused()
    {
        Assert.Equal(PerkCommitResult.DuplicateNode, Remote(5).TryCommitExact(new[] { "util_1", "util_1" }));
        Assert.Equal(PerkCommitResult.NotEnoughPoints, Remote(1).TryCommitExact(new[] { "util_1", "util_2" }));

        var s = Remote(5);
        Assert.Equal(PerkCommitResult.Ok, s.TryCommitExact(new[] { "util_1" }));
        Assert.Equal(PerkCommitResult.AlreadyCommitted, s.TryCommitExact(new[] { "util_1" }));
    }

    [Fact]
    public void A_refused_commit_changes_nothing_on_the_sender_or_the_remote()
    {
        var sender = Sender(2, "util_1", "util_2");
        var remote = Remote(2);
        var bad = new[] { "util_1", "gen_3" };          // gen_3 is nowhere near anything committed

        Assert.Equal(PerkCommitResult.NotConnected, sender.TryCommitExact(bad));
        Assert.Equal(PerkCommitResult.NotConnected, remote.TryCommitExact(bad));

        Assert.Empty(sender.Committed);
        Assert.Empty(remote.Committed);
        Assert.Equal(2, sender.UnspentPoints);          // the sender's pending nodes were refunded, not lost
        Assert.Equal(sender.SyncHash(), remote.SyncHash());
        Assert.Equal(sender.UnspentPoints, remote.UnspentPoints);
    }

    [Fact]
    public void An_oversized_list_is_refused_before_anything_else_is_looked_at()
    {
        var huge = Enumerable.Repeat("util_1", PerkState.MaxCommitNodes + 1).ToList();
        Assert.Equal(PerkCommitResult.TooManyNodes, Remote(100).TryCommitExact(huge));
    }

    [Fact]
    public void Counters_ride_along_untouched_by_a_commit()
    {
        var s = Remote(3);
        s.Counters.Add("energy_per_attacks.attacks", 7);
        s.TryCommitExact(new[] { "util_1" });
        Assert.Equal(7, s.Counters.Get("energy_per_attacks.attacks"));
    }
}
