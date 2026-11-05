using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace PerkTree.Mod;

[HarmonyPatch(typeof(NMapScreen), "_Ready")]
internal static class MapPerkButtonReadyPatch
{
    private static void Postfix(NMapScreen __instance) => PerkMapUi.Ensure(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class MapPerkButtonOpenPatch
{
    private static void Postfix(NMapScreen __instance) => PerkMapUi.Ensure(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled))]
internal static class MapPerkButtonTravelPatch
{
    private static void Postfix(NMapScreen __instance) => PerkMapUi.Ensure(__instance);
}

// A map can be ready before the run HUD. Retry after the native top bar has acquired its controls.
[HarmonyPatch(typeof(NTopBar), "_Ready")]
internal static class TopBarPerkButtonReadyPatch
{
    private static void Postfix(NTopBar __instance) => Callable.From(() => PerkMapUi.EnsureTopBar(__instance)).CallDeferred();
}

internal static class PerkMapUi
{
    internal const string ButtonName = "PerkTreeButton";

    public static void Ensure(NMapScreen map)
    {
        var topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar != null) EnsureTopBar(topBar);
    }

    internal static void EnsureTopBar(NTopBar topBar)
    {
        try
        {
            // Use the native map button's actual parent, including if another mod moved that row.
            if (topBar.Map?.GetParent() is not HBoxContainer row) return;
            if (row.GetNodeOrNull<NPerkTopBarButton>(ButtonName) != null) return;
            var button = NPerkTopBarButton.Create(topBar);
            row.AddChild(button);
            row.MoveChild(button, topBar.Map.GetIndex());
            MainFile.Log("native Perks button added beside Map in the top bar");
        }
        catch (Exception e) { MainFile.LogWarn($"could not add the native Perks button: {e}"); }
    }
}
