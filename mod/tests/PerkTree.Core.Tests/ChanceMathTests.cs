using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class ChanceMathTests
{
    [Theory]
    [InlineData(0.25, 2500)]
    [InlineData(0.3333, 3333)]
    [InlineData(1.0, 10000)]
    [InlineData(1.5, 10000)]     // an over-cap total can never exceed certainty
    [InlineData(0.0, 0)]
    [InlineData(-0.2, 0)]
    public void Probability_becomes_whole_basis_points(double probability, int expected)
        => Assert.Equal(expected, ChanceMath.ToBasisPoints((decimal)probability));

    [Fact]
    public void Halves_round_away_from_zero_on_every_machine()
    {
        Assert.Equal(3334, ChanceMath.ToBasisPoints(0.33335m));
        Assert.Equal(1, ChanceMath.ToBasisPoints(0.00005m));
        Assert.Equal(0, ChanceMath.ToBasisPoints(0.00004m));
    }

    [Theory]
    [InlineData(0.0, 0, 0.0)]
    [InlineData(-1.0, 0, 0.0)]
    [InlineData(0.5, 0, 0.5)]
    [InlineData(1.0, 1, 0.0)]
    [InlineData(1.5, 1, 0.5)]
    [InlineData(2.3, 2, 0.3)]
    public void Split_turns_a_chance_past_100_percent_into_sure_procs_plus_a_roll(double chance, int sure, double rest)
    {
        var (guaranteed, remainder) = ChanceMath.Split((decimal)chance);
        Assert.Equal(sure, guaranteed);
        Assert.Equal((decimal)rest, remainder);
    }

    [Fact]
    public void A_runaway_chance_is_capped_so_it_can_never_loop_forever()
    {
        var (guaranteed, remainder) = ChanceMath.Split(1_000_000m);
        Assert.Equal(ChanceMath.MaxGuaranteed, guaranteed);
        Assert.Equal(0m, remainder);
    }
}
