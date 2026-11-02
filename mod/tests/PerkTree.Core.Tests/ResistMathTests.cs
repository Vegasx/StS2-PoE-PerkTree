using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class ResistMathTests
{
    [Fact]
    public void Vulnerable_loses_points_from_the_bonus()
    {
        Assert.Equal(1.4m, ResistMath.Vulnerable(1.5m, 0.10m));   // the design example: 50% -> 40%
        Assert.Equal(1.2m, ResistMath.Vulnerable(1.5m, 0.30m));
    }

    [Fact]
    public void Vulnerable_stops_at_neutral_and_never_becomes_damage_reduction()
    {
        Assert.Equal(1.0m, ResistMath.Vulnerable(1.5m, 0.50m));   // exactly cancelled
        Assert.Equal(1.0m, ResistMath.Vulnerable(1.5m, 0.90m));   // overshoot is wasted, not a bonus
    }

    [Fact]
    public void Weak_and_Frail_penalties_shrink_toward_neutral_and_stop_there()
    {
        Assert.Equal(0.85m, ResistMath.Penalty(0.75m, 0.10m));
        Assert.Equal(1.0m, ResistMath.Penalty(0.75m, 0.25m));
        Assert.Equal(1.0m, ResistMath.Penalty(0.75m, 0.60m));
    }

    [Fact]
    public void Multipliers_the_perk_does_not_apply_to_are_left_alone()
    {
        // The game also calls these methods when the debuff isn't in play (neutral 1.0); those results must pass through.
        Assert.Equal(1.0m, ResistMath.Vulnerable(1.0m, 0.50m));
        Assert.Equal(1.0m, ResistMath.Penalty(1.0m, 0.50m));
        Assert.Equal(1.5m, ResistMath.Vulnerable(1.5m, 0m));
        Assert.Equal(0.75m, ResistMath.Penalty(0.75m, 0m));
    }

    [Fact]
    public void An_amplifier_such_as_Debilitate_still_bites_a_fully_resistant_player()
    {
        // Suppose another effect already pushed Vulnerable to 2.0x. Resistance is applied last, so 0.50 of it leaves
        // 1.5x rather than granting immunity: the enemy keeps an answer to a stacked-resistance build.
        Assert.Equal(1.5m, ResistMath.Vulnerable(2.0m, 0.50m));
    }

    [Fact]
    public void Committed_version_changes_only_when_the_committed_set_changes()
    {
        var s = Fixture.OpenState(5);
        int v0 = s.CommittedVersion;

        s.TryAllocate("util_1");
        Assert.Equal(v0, s.CommittedVersion);          // pending is not committed

        s.Commit();
        int v1 = s.CommittedVersion;
        Assert.NotEqual(v0, v1);

        s.OpenAllocationWindow();
        s.Commit();                                    // nothing pending
        Assert.Equal(v1, s.CommittedVersion);
    }
}
