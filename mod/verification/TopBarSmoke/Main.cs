using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.TopBar;

// Temporary verification mod. Installed only for a headless check, then removed. Does not start a run.
[ModInitializer(nameof(Initialize))]
public static class Main
{
    public static void Initialize()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var timer = new Godot.Timer { WaitTime = 15, OneShot = true, Autostart = true, ProcessMode = Node.ProcessModeEnum.Always };
        timer.Timeout += () => Check(tree);
        Callable.From(() => tree.Root.AddChild(timer)).CallDeferred();
    }

    private static void Check(SceneTree tree)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "PerkTree");
            var ui = assembly.GetType("PerkTree.Mod.PerkMapUi", throwOnError: true)!;
            var ensure = ui.GetMethod("EnsureTopBar", BindingFlags.NonPublic | BindingFlags.Static)!;
            var map = PreloadManager.Cache.GetScene("res://scenes/ui/top_bar/top_bar_map_button.tscn").Instantiate<NTopBarMapButton>();
            var deck = PreloadManager.Cache.GetScene("res://scenes/ui/top_bar/top_bar_deck_button.tscn").Instantiate<NTopBarDeckButton>();
            map.Name = "Map";
            deck.Name = "Deck";
            var bar = new NTopBar(); // Proxy only; do not add an uninitialized run HUD to the scene tree.
            typeof(NTopBar).GetProperty("Map")!.SetValue(bar, map);
            typeof(NTopBar).GetProperty("Deck")!.SetValue(bar, deck);
            var row = new HBoxContainer { Size = new Vector2(480, 80), Alignment = BoxContainer.AlignmentMode.End };
            row.AddChild(map);
            row.AddChild(deck);
            ensure.Invoke(null, [bar]);
            ensure.Invoke(null, [bar]);
            var buttons = row.GetChildren().Where(n => n.Name == "PerkTreeButton").ToArray();
            if (buttons.Length != 1) throw new Exception($"Expected one Perks button, got {buttons.Length}");
            var button = (NTopBarButton)buttons[0];
            if (button.GetIndex() != map.GetIndex() - 1) throw new Exception("Perks is not immediately before Map");
            // These templates have no Player/CardPile at the main menu. They are used for construction
            // and insertion checks only; do not run their run-specific enter/exit notifications.
            row.RemoveChild(map);
            row.RemoveChild(deck);
            tree.Root.AddChild(row); // Exercises the real Godot C# script registration and _Ready dispatch.
            if (!button.IsNodeReady() || !button.GetChildren().OfType<Godot.Timer>().Any())
                throw new Exception("Godot did not dispatch the custom button's _Ready");
            var icon = button.GetNode<TextureRect>("Control/Icon");
            if (icon.Texture == null || icon.Material == null) throw new Exception("Icon or native shader missing");
            if (ReferenceEquals(icon.Material, map.GetNode<TextureRect>("Control/Icon").Material))
                throw new Exception("Perks and Map share an animation material");
            if (button.IsEnabled) throw new Exception("Perks should be disabled without an active map/run");
            GD.Print($"[PerkTreeUiSmoke] PASS: native row, idempotence, order, Godot _Ready, icon {icon.Texture.GetWidth()}x{icon.Texture.GetHeight()}, isolated shader, availability");
            row.QueueFree();
            map.Free();
            deck.Free();
            bar.Free();
            tree.Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[PerkTreeUiSmoke] FAIL: {e}");
            tree.Quit(1);
        }
    }
}
