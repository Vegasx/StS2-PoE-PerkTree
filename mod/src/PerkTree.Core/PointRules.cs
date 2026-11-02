namespace PerkTree.Core;

/// <summary>Earning and spending rules from design doc section 5. Kept as constants in one place so balance tuning is a one-line change.</summary>
public static class PointRules
{
    public const int NormalCombat = 1;
    public const int Elite = 2;

    /// <summary>
    /// Every boss pays the same, final bosses included. A special case to skip them would be extra code
    /// for no benefit (over-awarded end-of-run points are harmless) and would break Ascension 10's two
    /// sequential final bosses.
    /// </summary>
    public const int Boss = 3;

    /// <summary>Every node costs one point; the doc's 35-45 point budget assumes it.</summary>
    public const int NodeCost = 1;

    public static int ForCombat(RoomKind kind) => kind switch
    {
        RoomKind.Normal => NormalCombat,
        RoomKind.Elite => Elite,
        RoomKind.Boss => Boss,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
