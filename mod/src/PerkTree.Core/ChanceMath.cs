namespace PerkTree.Core;

/// <summary>
/// Turns a probability into a whole number of basis points (1 = 0.01%), so a roll is an integer comparison
/// ("random number in 0..9999 is below N") and no floating-point rounding can differ between co-op machines.
/// </summary>
public static class ChanceMath
{
    public const int Scale = 10000;

    /// <summary>
    /// Splits a chance that may exceed 100% into whole guaranteed procs and a remainder to roll for: 0.5 is "no sure procs,
    /// roll at 50%", 1.0 is "one sure proc", 2.3 is "two sure procs, roll at 30% for a third". This is what lets chance
    /// perks stack past 100%.
    /// </summary>
    public static (int Guaranteed, decimal Remainder) Split(decimal chance)
    {
        if (chance <= 0m) return (0, 0m);
        var whole = decimal.Floor(chance);
        if (whole >= MaxGuaranteed) return (MaxGuaranteed, 0m);
        return ((int)whole, chance - whole);
    }

    /// <summary>A safety ceiling on procs from a single roll, so a runaway stack can never become an endless loop.</summary>
    public const int MaxGuaranteed = 100;

    /// <summary>
    /// What an effect gives on one trigger: the sum of the amounts of its perks that succeeded. Each perk rolls on its own, in the
    /// order given. A perk at 100% (or more) gives its whole-number part as sure successes and rolls only the remainder; a perk at
    /// exactly 100% therefore never calls <paramref name="rollRemainder"/>, so an effect nobody made chancy uses no random numbers.
    /// The luck modifiers only touch chancy perks (chance below 1): <paramref name="boost"/> is added to the chance, then the result
    /// is multiplied by (1 + <paramref name="doubling"/>); <paramref name="tries"/> gives a failed remainder extra attempts. Successes
    /// are capped at <see cref="MaxGuaranteed"/> in total and the result at <paramref name="cap"/>.
    /// </summary>
    public static decimal RollTotal(IReadOnlyList<NodeRoll> perks, decimal boost, decimal doubling, int tries,
                                    Func<decimal, bool> rollRemainder, decimal? cap = null)
    {
        var total = 0m;
        var successes = 0;
        foreach (var perk in perks)
        {
            var chance = perk.Chance < 1m ? ModifierMath.ProcChance(perk.Chance, boost, doubling) : perk.Chance;
            var (sure, remainder) = Split(chance);
            var won = sure;
            if (remainder > 0)
                for (var i = 0; i < tries; i++)
                    if (rollRemainder(remainder)) { won++; break; }
            won = Math.Min(won, MaxGuaranteed - successes);
            successes += won;
            total += won * perk.Amount;
            if (successes >= MaxGuaranteed) break;
        }
        return cap is { } c ? Math.Min(total, c) : total;
    }

    /// <summary>Data check for a chance in a catalog or tree: above zero (a perk that can never succeed is a mistake) and within the safety ceiling.</summary>
    public static bool IsValidChance(decimal chance) => chance > 0m && chance <= MaxGuaranteed;

    /// <summary>0 for 0% or less, <see cref="Scale"/> for 100% or more, otherwise the probability rounded half away from zero.</summary>
    public static int ToBasisPoints(decimal probability)
    {
        if (probability <= 0m) return 0;
        if (probability >= 1m) return Scale;
        return (int)Math.Round(probability * Scale, MidpointRounding.AwayFromZero);
    }
}
