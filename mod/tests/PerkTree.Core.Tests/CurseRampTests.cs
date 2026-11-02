using PerkTree.Core;
using Xunit;

namespace PerkTree.Core.Tests;

public class CurseRampTests
{
    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(-3, 0, 0, false)]
    [InlineData(1, 1, 0, false)]
    [InlineData(2, 1, 1, false)]
    [InlineData(3, 2, 2, false)]
    [InlineData(4, 3, 2, false)]
    [InlineData(5, 3, 3, true)]
    [InlineData(9, 3, 3, true)]    // five or more all use the last row
    public void The_ramp_matches_the_audited_table(int curses, int str, int dex, bool intangible)
        => Assert.Equal((str, dex, intangible), CurseRamp.For(curses));
}
