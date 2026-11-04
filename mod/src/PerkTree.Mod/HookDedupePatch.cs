using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace PerkTree.Mod;

/// <summary>
/// RunState.IterateHookListeners yields the run subscribers (our hosts) and then, during combat, the combat's own listeners, which
/// now include our hosts again (combat subscription, see MainFile). Without this every run-level hook in combat (BeforeCombatStart,
/// AfterCombatVictory, rewards...) would reach each host twice: double points, double rewards. The postfix wraps the result and
/// drops the second copy of any perk host. Cheap: one small iterator per call, and it does nothing extra while no host exists.
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
internal static class HookDedupePatch
{
    private static void Postfix(ref IEnumerable<AbstractModel> __result)
    {
        if (!PerkRuntime.CurrentHosts.Any()) return;
        __result = SkipRepeatedHosts(__result);
    }

    private static IEnumerable<AbstractModel> SkipRepeatedHosts(IEnumerable<AbstractModel> source)
    {
        // At most a handful of players, so a short list beats a HashSet allocation.
        List<PerkHostModel>? seen = null;
        foreach (var model in source)
        {
            if (model is PerkHostModel host)
            {
                seen ??= new List<PerkHostModel>(4);
                if (seen.Contains(host)) continue;
                seen.Add(host);
            }
            yield return model;
        }
    }
}
