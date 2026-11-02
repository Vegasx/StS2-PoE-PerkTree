using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class RollTotalTests
{
    private static NodeRoll N(decimal amount, decimal chance) => new(amount, chance);

    // A "dice" that always fails / always succeeds, and one that counts how often it was asked.
    private static readonly Func<decimal, bool> Never = _ => false;
    private static readonly Func<decimal, bool> Always = _ => true;

    [Fact]
    public void Guaranteed_perks_just_add_up_and_never_touch_the_dice()
    {
        var asked = 0;
        var total = ChanceMath.RollTotal(new[] { N(2, 1), N(3, 1) }, 0, 0, 1, _ => { asked++; return false; });
        Assert.Equal(5m, total);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Nodes_of_one_effect_can_give_different_amounts()
    {
        // Per-node amounts: a sure 1, a sure 2.5 and a 50% node worth 4 that wins its roll all add up; a failed one adds nothing.
        var wins = ChanceMath.RollTotal(new[] { N(1, 1), N(2.5m, 1), N(4, 0.5m) }, 0, 0, 1, Always);
        Assert.Equal(7.5m, wins);
        var loses = ChanceMath.RollTotal(new[] { N(1, 1), N(2.5m, 1), N(4, 0.5m) }, 0, 0, 1, Never);
        Assert.Equal(3.5m, loses);
        // Past 100% each node keeps its own amount for its sure successes: 225% of 2 is two sure 2s, plus the 25% roll.
        var over = ChanceMath.RollTotal(new[] { N(2, 2.25m), N(10, 1) }, 0, 0, 1, Always);
        Assert.Equal(16m, over);
    }

    [Fact]
    public void Each_chancy_perk_rolls_on_its_own()
    {
        // Three 50% perks of 2 each; the dice say yes, no, yes.
        var answers = new Queue<bool>(new[] { true, false, true });
        var total = ChanceMath.RollTotal(new[] { N(2, 0.5m), N(2, 0.5m), N(2, 0.5m) }, 0, 0, 1, _ => answers.Dequeue());
        Assert.Equal(4m, total);
        Assert.Empty(answers);   // exactly one roll per perk
    }

    [Fact]
    public void A_chance_past_one_hundred_gives_sure_successes_and_rolls_the_rest()
    {
        var rolledWith = new List<decimal>();
        var total = ChanceMath.RollTotal(new[] { N(1, 2.3m) }, 0, 0, 1, c => { rolledWith.Add(c); return true; });
        Assert.Equal(3m, total);                 // two sure + the 30% one succeeded
        Assert.Equal(new[] { 0.3m }, rolledWith);
    }

    [Fact]
    public void Luck_perks_change_only_chancy_perks()
    {
        var rolledWith = new List<decimal>();
        // The guaranteed perk is untouched by boost 0.1 and doubling 1 (it would be 2.2 otherwise); the 50% one becomes (0.5+0.1)*2 = 1.2.
        var total = ChanceMath.RollTotal(new[] { N(1, 1), N(1, 0.5m) }, 0.1m, 1m, 1, c => { rolledWith.Add(c); return false; });
        Assert.Equal(2m, total);                // 1 from the guaranteed perk, 1 sure success from 120%
        Assert.Equal(new[] { 0.2m }, rolledWith);
    }

    [Fact]
    public void Rerolls_give_a_failed_remainder_more_tries_and_stop_at_the_first_success()
    {
        var asked = 0;
        var total = ChanceMath.RollTotal(new[] { N(1, 0.5m) }, 0, 0, 3, _ => ++asked == 2);
        Assert.Equal(1m, total);
        Assert.Equal(2, asked);
        Assert.Equal(0m, ChanceMath.RollTotal(new[] { N(1, 0.5m) }, 0, 0, 3, Never));
    }

    [Fact]
    public void A_capped_effect_never_gives_more_than_its_cap()
        => Assert.Equal(1m, ChanceMath.RollTotal(new[] { N(1, 1), N(1, 1), N(1, 1) }, 0, 0, 1, Always, cap: 1m));

    [Fact]
    public void Total_successes_are_limited_so_a_huge_stack_cannot_run_away()
        => Assert.Equal(100m, ChanceMath.RollTotal(new[] { N(1, 80), N(1, 80) }, 0, 0, 1, Always));

    [Fact]
    public void A_perk_at_zero_amount_or_no_perks_gives_nothing()
    {
        Assert.Equal(0m, ChanceMath.RollTotal(Array.Empty<NodeRoll>(), 0, 0, 1, Always));
        Assert.Equal(0m, ChanceMath.RollTotal(new[] { N(0, 1) }, 0, 0, 1, Always));
    }
}
