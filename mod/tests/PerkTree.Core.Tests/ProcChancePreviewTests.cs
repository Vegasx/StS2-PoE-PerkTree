using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class ProcChancePreviewTests
{
    [Fact]
    public void Two_seven_percent_attempts_give_thirteen_point_five_one_percent()
    {
        var odds = ProcChancePreview.Calculate(0.07m, 0, 0, 1);
        Assert.Equal(0.1351m, odds.AtLeastOneChance);
        Assert.Equal(0.07m, odds.RemainderChance);
        Assert.Equal(2, odds.Rolls);
    }

    [Fact]
    public void Priming_and_multiplier_apply_before_reroll_probability()
    {
        var odds = ProcChancePreview.Calculate(0.1m, 0.05m, 0.5m, 1);
        Assert.Equal(0.225m, odds.RemainderChance);
        Assert.Equal(0.399375m, odds.AtLeastOneChance);
    }

    [Fact]
    public void Frenzied_extra_try_combines_with_rerolls_and_obeys_runtime_limit()
    {
        Assert.Equal(3, ProcChancePreview.Calculate(0.1m, 0, 0, 1, extraTries: 1).Rolls);
        var capped = ProcChancePreview.Calculate(0.1m, 0, 0, 100, extraTries: 1);
        Assert.Equal(ModifierMath.MaxRolls, capped.Rolls);
        Assert.Equal(0.40951m, capped.AtLeastOneChance);
    }

    [Fact]
    public void Guaranteed_and_nonrolling_perks_are_not_boosted()
    {
        Assert.Equal(1, ProcChancePreview.Calculate(1m, 0.8m, 2m, 1).GuaranteedProcs);
        var passive = ProcChancePreview.Calculate(0.1m, 0.8m, 2m, 1, canRoll: false);
        Assert.Equal(1, passive.GuaranteedProcs);
        Assert.Equal(0m, passive.ExtraProcChance);
    }

    [Fact]
    public void Over_one_hundred_percent_keeps_guaranteed_procs_and_extra_success_separate()
    {
        var odds = ProcChancePreview.Calculate(0.5m, 0.1m, 1m, 1);
        Assert.Equal(1, odds.GuaranteedProcs);
        Assert.Equal(1m, odds.AtLeastOneChance);
        Assert.Equal(0.2m, odds.RemainderChance);
        Assert.Equal(0.36m, odds.ExtraProcChance);
        // Already-over-100% nodes ignore boosts, but their fractional remainder still gets retries.
        var existing = ProcChancePreview.Calculate(1.2m, 99, 99, 1);
        Assert.Equal(odds, existing);
    }

    [Fact]
    public void Preview_uses_the_same_rounding_as_the_game_rng()
    {
        var rounded = ProcChancePreview.Calculate(0.12345m, 0, 0, 1);
        Assert.Equal(0.1235m, rounded.RemainderChance);
        Assert.Equal(1m - 0.8765m * 0.8765m, rounded.AtLeastOneChance);
        Assert.Equal(0m, ProcChancePreview.Calculate(0.00004m, 0, 0, 4).AtLeastOneChance);
    }

    [Fact]
    public void Combined_odds_match_exhaustive_runtime_roll_outcomes()
    {
        // Enumerate all two-roll outcomes with a fair four-sided die. 25% per attempt => 7/16 succeeds.
        var successes = 0;
        for (var first = 0; first < 4; first++)
            for (var second = 0; second < 4; second++)
            {
                var dice = new Queue<int>([first, second]);
                var amount = ChanceMath.RollTotal([new NodeRoll(1, 0.25m)], 0, 0, 2,
                    p => dice.Dequeue() / 4m < p);
                if (amount > 0) successes++;
            }
        Assert.Equal(successes / 16m, ProcChancePreview.Calculate(0.25m, 0, 0, 1).AtLeastOneChance);
    }
}
