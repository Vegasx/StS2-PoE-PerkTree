namespace PerkTree.Core;

/// <summary>
/// The arithmetic behind the "meta" perks that change OTHER perks (proc_boost, proc_double, proc_reroll, potion_boost,
/// potion_double, start_power_boost). It lives here, away from the game, so it can be unit tested and so every client
/// is guaranteed to use the same formula.
/// </summary>
public static class ModifierMath
{
    /// <summary>Most rolls one proc may make with proc_reroll, so a big total can't turn a proc into a loop.</summary>
    public const int MaxRolls = 5;

    /// <summary>
    /// A random proc's chance after the player's modifiers. <paramref name="boost"/> is proc_boost (0.10 = +10 points),
    /// added first; <paramref name="doubleTotal"/> is proc_double, which multiplies by (1 + total) so one node makes procs
    /// twice as likely and two nodes three times. NOT clamped to 100%: a result of 1.2 means one sure proc and a 20% roll
    /// for another (<see cref="ChanceMath.Split"/>). A proc that was impossible stays impossible.
    /// </summary>
    public static decimal ProcChance(decimal chance, decimal boost, decimal doubleTotal)
    {
        if (chance <= 0m) return 0m;
        return (chance + Math.Max(0m, boost)) * (1m + Math.Max(0m, doubleTotal));
    }

    /// <summary>How many rolls a proc gets: one, plus one per whole point of proc_reroll, plus <paramref name="extra"/> (Frenzied gives the Vigor-spending perks one), up to <see cref="MaxRolls"/>.</summary>
    public static int ProcRolls(decimal rerollTotal, int extra = 0)
        => Math.Min(MaxRolls, 1 + (int)Math.Min(MaxRolls - 1, Math.Floor(Math.Max(0m, rerollTotal))) + Math.Max(0, extra));

    /// <summary>
    /// The amount a potion-use perk grants: potion_boost is added to the base, then potion_double doubles the result.
    /// A base of zero (the perk isn't owned) stays zero, so the boosts alone never create an effect.
    /// </summary>
    public static decimal PotionAmount(decimal baseAmount, decimal boost, decimal doubleTotal)
    {
        if (baseAmount <= 0m) return 0m;
        var value = baseAmount + Math.Max(0m, boost);
        return doubleTotal > 0m ? value * 2m : value;
    }

    /// <summary>The same boost rule for the combat-start defensive perks (start_power_boost): +boost, only when the perk itself is owned.</summary>
    public static decimal BoostedStartAmount(decimal baseAmount, decimal boost)
        => baseAmount <= 0m ? 0m : baseAmount + Math.Max(0m, boost);
}
