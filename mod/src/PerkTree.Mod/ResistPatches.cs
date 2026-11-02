using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod;

// "Vulnerable / Weak / Frail is N points less effective on you" (Gamble branch).
//
// Why Harmony here: the game computes these inside VulnerablePower / WeakPower / FrailPower and hardcodes its own
// modifiers (Paper Phrog, Cruelty, Debilitate...) by name, so there is no model hook to plug into. A postfix that
// adjusts the finished multiplier is exact whatever those modifiers did (see ResistMath for why "applied last").
//
// Cost: these run per damage / block calculation (including card previews), NOT per frame. Each one bails on its
// first line unless the debuff is actually changing the number, and then does two dictionary lookups, so they are
// effectively free for anyone without the perk.

[HarmonyPatch(typeof(VulnerablePower), nameof(VulnerablePower.ModifyDamageMultiplicative))]
internal static class VulnerableResistPatch
{
    private static void Postfix(VulnerablePower __instance, ref decimal __result)
    {
        // The game also calls this when the hit isn't on the owner or isn't a powered attack; those return neutral (1).
        if (__result <= 1m) return;
        var points = PerkRuntime.ResistFor(__instance.Owner, "vulnerable_resist");
        if (points > 0m) __result = ResistMath.Vulnerable(__result, points);
    }
}

[HarmonyPatch(typeof(WeakPower), nameof(WeakPower.ModifyDamageMultiplicative))]
internal static class WeakResistPatch
{
    private static void Postfix(WeakPower __instance, ref decimal __result)
    {
        if (__result >= 1m) return;
        var points = PerkRuntime.ResistFor(__instance.Owner, "weak_resist");
        if (points > 0m) __result = ResistMath.Penalty(__result, points);
    }
}

[HarmonyPatch(typeof(FrailPower), nameof(FrailPower.ModifyBlockMultiplicative))]
internal static class FrailResistPatch
{
    private static void Postfix(FrailPower __instance, ref decimal __result)
    {
        if (__result >= 1m) return;
        var points = PerkRuntime.ResistFor(__instance.Owner, "frail_resist");
        if (points > 0m) __result = ResistMath.Penalty(__result, points);
    }
}
