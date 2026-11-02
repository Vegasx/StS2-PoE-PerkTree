using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace PerkTree.Mod;

/// <summary>
/// The map screen is where allocation happens and where voting locks it (design doc section 6). Parameters are
/// injected by position (__0, __1...) because the game's parameter names are not part of what we can see.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled))]
internal static class MapTravelEnabledPatch
{
    // Travel being enabled is "the party may choose where to go next", which is exactly when perks may be spent.
    // (Not Open(): the map can also be peeked at mid-room, where allocation must stay closed.)
    private static void Postfix(bool __0)
    {
        if (__0) PerkRuntime.OnMapTravelEnabled();
    }
}

[HarmonyPatch(typeof(NMapScreen), "OnMapPointSelectedLocally")]
internal static class MapLocalSelectPatch
{
    // A PREFIX, on purpose: this method builds the game's vote action and enqueues it inside itself. Our commit action must
    // be enqueued BEFORE that vote, in the same per-player queue, so it runs first on every client; after the last player's
    // vote the party travels and it would be too late. The game IL shows this is the single local-vote path (it creates
    // VoteForMapCoordAction and calls RequestEnqueue itself). The IsTravelEnabled guard keeps a stray click from locking.
    private static void Prefix(NMapScreen __instance)
    {
        if (__instance.IsTravelEnabled) PerkRuntime.OnLocalPlayerVoted();
    }
}
