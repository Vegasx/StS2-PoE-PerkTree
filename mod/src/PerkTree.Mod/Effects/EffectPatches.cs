using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using PerkTree.Mod.Effects;

namespace PerkTree.Mod;

// The Harmony patches for effects that have no game hook. Parameters are injected by position (__0...) because the game's own names
// aren't something we can rely on. All three fire on a discrete game event (a card exhausting, energy being gained, Doom killing),
// never per frame, and each bails immediately when the player has no relevant perk.

/// <summary>exhaust_dodge and ethereal_save: swap the exhaust for a plain discard.</summary>
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Exhaust))]
internal static class ExhaustDodgePatch
{
    // __0 = choice context, __1 = the card, __2 = whether Ethereal caused it.
    private static bool Prefix(ref Task<CardPileAddResult?> __result, PlayerChoiceContext __0, CardModel __1, bool __2)
    {
        try
        {
            var host = PerkRuntime.HostOf(__1?.Owner);
            if (host == null) return true;

            // Ethereal first: it needs no roll, so whether we roll below is decided the same way on every client.
            var dodge = (__2 && host.Handler<EtherealSave>() != null)
                        || (host.Handler<ExhaustDodge>() is { } chance && chance.Dodges());
            if (!dodge) return true;

            __result = DiscardInstead(__0, __1!);
            return false;   // skip the exhaust
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"exhaust dodge failed (exhaust kept): {e.GetType().Name}: {e.Message}");
            return true;
        }
    }

    private static async Task<CardPileAddResult?> DiscardInstead(PlayerChoiceContext ctx, CardModel card)
    {
        await CardCmd.Discard(ctx, card);
        return null;   // the callers treat the result as optional
    }
}

/// <summary>energy_discharge: after an awaited GainEnergy, discharge. Chained onto the command's own Task so it runs in order.</summary>
[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainEnergy))]
internal static class EnergyDischargePatch
{
    // __0 = amount, __1 = the player.
    private static void Postfix(ref Task __result, decimal __0, Player __1)
    {
        var discharge = PerkRuntime.HostOf(__1)?.Handler<EnergyDischarge>();
        if (discharge != null && __0 > 0) __result = After(__result, discharge, __0);
    }

    private static async Task After(Task original, EnergyDischarge discharge, decimal amount)
    {
        await original;
        try { await discharge.Discharge(amount); }
        catch (Exception e) { MainFile.LogWarn($"energy discharge failed: {e.GetType().Name}: {e.Message}"); }
    }
}

/// <summary>doom_defer: take the owner's body off Doom's kill list. Filtering DoomKill (not the doomed list) keeps a doomed summon dying.</summary>
[HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
internal static class DoomDeferPatch
{
    // __0 = the creatures about to be killed by Doom (may be replaced).
    private static void Prefix(ref IReadOnlyList<Creature> __0)
    {
        try
        {
            if (__0 == null || __0.Count == 0) return;
            List<Creature>? kept = null;
            foreach (var creature in __0)
            {
                var player = creature.Player;
                if (player == null || !ReferenceEquals(creature, player.Creature)) continue;   // only a player's own body
                if (PerkRuntime.HostOf(player)?.Handler<DoomDefer>() is { } defer && defer.Spares(creature))
                    (kept ??= new List<Creature>(__0)).Remove(creature);
            }
            if (kept != null) __0 = kept;   // may be empty, which DoomKill handles (it checks the count first)
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"doom defer failed (Doom unchanged): {e.GetType().Name}: {e.Message}");
        }
    }
}
