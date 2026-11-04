using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace PerkTree.Mod;

/// <summary>
/// UI slice 1: a "Perks" button on the map screen that opens a small summary panel (points, window state, committed/pending
/// counts and the effect totals). No tree drawing yet; allocation is still the `perk` console command. Built from plain Godot
/// controls added in NMapScreen._Ready, so it needs no scene file. A 0.5 s timer refreshes the text, so no per-frame patch is needed.
/// </summary>
// Where to attach. _Ready alone was not enough in game (no button, no error), most likely because the map screen is built
// before or outside what that patch saw. So every map entry point we know runs tries too; Ensure() is idempotent.
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

/// <summary>
/// The button and its panel, built from plain Godot nodes. Deliberately no Node subclass: a script class defined in a mod assembly
/// may not be registered with the engine (it needs Godot's source generator), whereas stock nodes and lambdas always work.
/// </summary>
internal static class PerkMapUi
{
    private const string NodeName = "PerkTreeUi";

    public static void Ensure(NMapScreen map)
    {
        try
        {
            if (map.GetNodeOrNull(NodeName) != null) return;   // already attached to this screen
            Build(map, NodeName);
            MainFile.Log("perk button added to the map screen");
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"could not add the perk button to the map screen: {e}");
        }
    }

    private static void Build(NMapScreen map, string name)
    {
        // Own CanvasLayer, drawn above the map's layers, so nothing on the map can cover the button. A CanvasLayer does not
        // inherit its parent's visibility, so Refresh shows it only while the map is open.
        var layer = new CanvasLayer { Name = name, Layer = 50, Visible = false };

        // Let clicks through everywhere except our own button/panel, so the map underneath keeps working.
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var button = new Button { Text = "Perks", CustomMinimumSize = new Vector2(120, 44) };
        root.AddChild(button);

        var panel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(380, 0) };
        var text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var pad = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) pad.AddThemeConstantOverride($"margin_{side}", 12);
        pad.AddChild(text);
        panel.AddChild(pad);
        root.AddChild(panel);

        void Refresh()
        {
            // Not during combat: the map screen can stay "open" underneath a fight, and the tree is a between-rooms thing.
            var inCombat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress == true;
            layer.Visible = map.IsOpen && map.IsVisibleInTree() && !inCombat;
            if (!layer.Visible) PerkTreeScreen.Hide(map);   // the tree never outlives the map

            // Placed by hand from the viewport size: anchors did nothing useful here (the root has no Control parent under the
            // CanvasLayer, so "top right" ended up half off screen). Below the top bar, clear of the right edge; redone every
            // refresh so a window resize is picked up.
            var view = root.GetViewportRect().Size;
            button.Position = new Vector2(view.X - button.Size.X - 60, 120);
            panel.Position = new Vector2(view.X - panel.Size.X - 60, 120 + button.Size.Y + 8);
            var host = PerkRuntime.LocalHost();
            button.Visible = host != null;   // no run, or no tree for this character
            if (host == null) { panel.Visible = false; return; }

            var s = host.State;
            button.Text = s.UnspentPoints > 0 ? $"Perks ({s.UnspentPoints})" : "Perks";
            if (!panel.Visible) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Unspent points: {s.UnspentPoints}");
            sb.AppendLine($"Allocation window: {(s.AllocationOpen ? "open" : "closed")}");
            sb.AppendLine($"Perks taken: {s.Committed.Count}   Pending: {s.Pending.Count}");
            foreach (var t in s.GetTotals(includePending: true).Values)
                sb.AppendLine($"  {t.EffectId}: {t.Effective}{(t.Cap is { } c ? $" / {c}" : "")}");
            text.Text = sb.ToString().TrimEnd();
        }

        // The summary panel is superseded by the tree screen (its top bar shows the same numbers); the panel stays hidden.
        button.Pressed += () => PerkTreeScreen.Toggle(map);

        // Refresh on a timer rather than patching _Process: the data only changes on map-screen time scales.
        var timer = new Godot.Timer { WaitTime = 0.5, Autostart = true };
        timer.Timeout += Refresh;
        root.AddChild(timer);

        map.AddChild(layer);
        Refresh();
    }
}
