namespace PerkTree.Core;

/// <summary>Read-only odds for one node on one trigger, using the runtime's modifiers, limits and basis-point rounding.</summary>
public readonly record struct ProcChancePreview(int GuaranteedProcs, decimal RemainderChance, int Rolls, decimal ExtraProcChance)
{
    public decimal AtLeastOneChance => GuaranteedProcs > 0 ? 1m : ExtraProcChance;

    public static ProcChancePreview Calculate(decimal baseChance, decimal boost, decimal doubling, decimal rerolls,
                                             int extraTries = 0, bool canRoll = true)
    {
        // Like RollTotal: additive/multiplicative boosts only affect nodes below 100%.
        var adjusted = !canRoll ? 1m : baseChance < 1m ? ModifierMath.ProcChance(baseChance, boost, doubling) : baseChance;
        var (guaranteed, remainder) = ChanceMath.Split(adjusted);
        // PerkRng.Chance uses integer basis points, rather than the unrounded catalog value.
        var perRoll = ChanceMath.ToBasisPoints(remainder) / (decimal)ChanceMath.Scale;
        var rolls = ModifierMath.ProcRolls(rerolls, extraTries);
        var allFail = 1m;
        for (var i = 0; i < rolls; i++) allFail *= 1m - perRoll;
        return new ProcChancePreview(guaranteed, perRoll, rolls, 1m - allFail);
    }
}
