namespace PerkTree.Core;

/// <summary>
/// The arithmetic behind "Vulnerable / Weak / Frail is N points less effective on you" (Gamble branch).
///
/// These work on the multiplier the game has ALREADY computed, after every other modifier (relics, Debilitate...)
/// has had its say. Two reasons that is the right place:
///  - the game hardcodes those modifiers inside VulnerablePower/WeakPower by name, so there is no way to slot in
///    "before" them, and reading the final number is always accurate whatever they did;
///  - it keeps the perk predictable: the tooltip can say "-10 points" and mean exactly that. A rare amplifier such
///    as Debilitate therefore still bites a fully-resistant player a little (resistance is applied last), which
///    leaves the enemy an answer to a stacked-resistance build instead of making it flatly immune.
/// </summary>
public static class ResistMath
{
    /// <summary>
    /// Vulnerable: a damage-TAKEN multiplier above 1 (1.5 in the base game). <paramref name="points"/> is in the same
    /// units as the multiplier, so 0.10 ("10 points") takes 1.5 down to 1.4. It never goes below neutral:
    /// resistance cannot turn Vulnerable into damage reduction.
    /// </summary>
    public static decimal Vulnerable(decimal multiplier, decimal points) =>
        multiplier <= 1m || points <= 0m ? multiplier : Math.Max(1m, multiplier - points);

    /// <summary>
    /// Weak (damage dealt) and Frail (block gained): multipliers below 1 (0.75 in the base game). 0.10 takes 0.75 up
    /// to 0.85, and it never goes above neutral.
    /// </summary>
    public static decimal Penalty(decimal multiplier, decimal points) =>
        multiplier >= 1m || points <= 0m ? multiplier : Math.Min(1m, multiplier + points);
}
