namespace PerkTree.Core;

/// <summary>
/// Cursebound's combat-start package, by how many Curse cards are in the deck. It is the old mod's table, unchanged:
/// 1 curse +1 Str; 2 +1 Str +1 Dex; 3 +2/+2; 4 +3/+2; 5 or more +3/+3 and 1 Intangible. Kept in Core so the table is tested
/// and a balance change is a one-line edit.
/// </summary>
public static class CurseRamp
{
    // Index = number of curses, 0 to 5; five or more all use the last row.
    private static readonly int[] Strength = { 0, 1, 1, 2, 3, 3 };
    private static readonly int[] Dexterity = { 0, 0, 1, 2, 2, 3 };
    private const int IntangibleAt = 5;

    public static (int Strength, int Dexterity, bool Intangible) For(int curses)
    {
        if (curses <= 0) return (0, 0, false);
        var row = Math.Min(curses, Strength.Length - 1);
        return (Strength[row], Dexterity[row], row >= IntangibleAt);
    }
}
