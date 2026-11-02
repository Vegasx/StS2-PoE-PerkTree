using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class PerkRunSaveTests
{
    private static PerkState StateWith(params string[] committed)
    {
        var s = Fixture.OpenState(committed.Length + 3);
        foreach (var id in committed) Assert.Equal(PerkOpResult.Ok, s.TryAllocate(id));
        s.Commit();
        return s;
    }

    [Fact]
    public void Round_trips_all_players_through_json()
    {
        var a = StateWith("util_1", "util_2");
        var b = StateWith("poison_1");
        var save = PerkRunSave.Capture(12345L, new[] { (111UL, "SILENT", a), (222UL, "SILENT", b) });

        Assert.True(PerkRunSave.TryParse(save.ToJson(), out var loaded));
        Assert.Equal(12345L, loaded!.RunStartTime);
        Assert.Equal(2, loaded.Players.Count);
        Assert.Equal(a.CommittedHash(), loaded.For(111UL, "SILENT")!.CommittedHash);
        Assert.Equal(new[] { "util_1", "util_2" }, loaded.For(111UL, "SILENT")!.State.Committed);
        Assert.Equal(a.UnspentPoints, loaded.For(111UL, "SILENT")!.State.Unspent);
    }

    [Fact]
    public void A_saved_state_restores_to_the_same_perks_and_totals()
    {
        var original = StateWith("util_1", "util_2", "gen_1", "gen_2", "gen_3", "gen_4");
        var save = PerkRunSave.Capture(1L, new[] { (7UL, "SILENT", original) });
        Assert.True(PerkRunSave.TryParse(save.ToJson(), out var loaded));

        var restored = PerkState.FromSave(Fixture.Graph(), loaded!.For(7UL, "SILENT")!.State, out var report);

        Assert.Empty(report.DroppedNodes);
        Assert.Equal(original.CommittedHash(), restored.CommittedHash());
        Assert.Equal(original.UnspentPoints, restored.UnspentPoints);
        Assert.Equal(0.10m, restored.GetTotal("vulnerable_resist").Effective);
    }

    [Fact]
    public void Pending_nodes_survive_a_save_taken_mid_allocation()
    {
        var s = Fixture.OpenState(5);
        s.TryAllocate("util_1");           // still pending: no Commit()
        var save = PerkRunSave.Capture(1L, new[] { (7UL, "SILENT", s) });
        Assert.True(PerkRunSave.TryParse(save.ToJson(), out var loaded));

        var restored = PerkState.FromSave(Fixture.Graph(), loaded!.For(7UL, "SILENT")!.State, out _);

        Assert.Equal(new[] { "util_1" }, restored.Pending);
        Assert.Empty(restored.Committed);          // quitting mid-allocation must not lock anything in
        Assert.Equal(4, restored.UnspentPoints);   // ...nor lose the point that was spent
    }

    [Fact]
    public void Lookup_requires_the_right_player_and_character()
    {
        var save = PerkRunSave.Capture(1L, new[] { (7UL, "SILENT", StateWith("util_1")) });
        Assert.NotNull(save.For(7UL, "silent"));    // character ids compare case-insensitively
        Assert.Null(save.For(8UL, "SILENT"));       // other player
        Assert.Null(save.For(7UL, "IRONCLAD"));     // same player id but a different character: not their tree
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"version\": 99, \"players\": []}")]   // from a newer mod version
    [InlineData("[1,2,3]")]
    public void Bad_or_foreign_files_are_rejected_without_throwing(string? json)
    {
        Assert.False(PerkRunSave.TryParse(json, out var save));
        Assert.Null(save);
    }

    [Fact]
    public void Output_is_stable_regardless_of_player_order()
    {
        var a = StateWith("util_1");
        var b = StateWith("poison_1");
        var one = PerkRunSave.Capture(1L, new[] { (111UL, "SILENT", a), (222UL, "SILENT", b) }).ToJson();
        var two = PerkRunSave.Capture(1L, new[] { (222UL, "SILENT", b), (111UL, "SILENT", a) }).ToJson();
        Assert.Equal(one, two);
    }
}
