using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class ModifierMathTests
{
    [Fact]
    public void Boost_adds_points_then_double_multiplies()
    {
        Assert.Equal(0.25m, ModifierMath.ProcChance(0.25m, 0m, 0m));
        Assert.Equal(0.35m, ModifierMath.ProcChance(0.25m, 0.10m, 0m));
        Assert.Equal(0.50m, ModifierMath.ProcChance(0.25m, 0m, 1m));     // one proc_double node = twice as likely
        Assert.Equal(0.70m, ModifierMath.ProcChance(0.25m, 0.10m, 1m));  // boost first, then doubled
        Assert.Equal(0.75m, ModifierMath.ProcChance(0.25m, 0m, 2m));     // two nodes = three times
    }

    [Fact]
    public void Chance_can_exceed_certainty_and_impossible_stays_impossible()
    {
        Assert.Equal(1.4m, ModifierMath.ProcChance(0.60m, 0.10m, 1m));   // (0.6 + 0.1) x 2: past 100%, the extra becomes more procs
        Assert.Equal(0m, ModifierMath.ProcChance(0m, 0.50m, 3m));
        Assert.Equal(0m, ModifierMath.ProcChance(-0.1m, 0.50m, 3m));
    }

    [Fact]
    public void Negative_modifiers_are_ignored_rather_than_trusted()
        => Assert.Equal(0.25m, ModifierMath.ProcChance(0.25m, -0.5m, -2m));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(99, ModifierMath.MaxRolls)]
    [InlineData(-4, 1)]
    public void Rerolls_add_whole_extra_rolls_up_to_the_cap(int total, int expected)
        => Assert.Equal(expected, ModifierMath.ProcRolls(total));

    [Fact]
    public void Potion_boost_is_added_then_double_doubles_the_result()
    {
        Assert.Equal(4m, ModifierMath.PotionAmount(4m, 0m, 0m));
        Assert.Equal(5m, ModifierMath.PotionAmount(4m, 1m, 0m));
        Assert.Equal(8m, ModifierMath.PotionAmount(4m, 0m, 1m));
        Assert.Equal(10m, ModifierMath.PotionAmount(4m, 1m, 1m));   // (4+1) doubled
    }

    [Fact]
    public void Boosts_alone_never_create_an_effect()
    {
        Assert.Equal(0m, ModifierMath.PotionAmount(0m, 3m, 1m));
        Assert.Equal(0m, ModifierMath.BoostedStartAmount(0m, 2m));
        Assert.Equal(3m, ModifierMath.BoostedStartAmount(2m, 1m));
    }

    [Fact]
    public void An_extra_try_adds_to_the_rerolls_but_never_passes_the_limit()
    {
        Assert.Equal(2, ModifierMath.ProcRolls(0m, extra: 1));          // Frenzied alone: one try plus one
        Assert.Equal(3, ModifierMath.ProcRolls(1m, extra: 1));          // plus a reroll perk
        Assert.Equal(ModifierMath.MaxRolls, ModifierMath.ProcRolls(10m, extra: 1));
        Assert.Equal(1, ModifierMath.ProcRolls(0m));                    // default unchanged
    }
}
