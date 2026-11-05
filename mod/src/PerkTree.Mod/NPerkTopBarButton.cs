using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.TopBar;

namespace PerkTree.Mod;

/// <summary>Uses the same native input, sound, hover/press animations and shader as the game's HUD buttons.</summary>
public partial class NPerkTopBarButton : NTopBarButton
{
    private const float IconSizeMultiplier = 1.05f;
    private MegaLabel? _points;
    private NMapScreen? _map;

    internal static NPerkTopBarButton Create(NTopBar topBar)
    {
        var button = new NPerkTopBarButton
        {
            Name = PerkMapUi.ButtonName,
            CustomMinimumSize = topBar.Map.CustomMinimumSize,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.All,
            PivotOffset = new Vector2(40, 40),
        };
        var control = new Control { Name = "Control", MouseFilter = MouseFilterEnum.Ignore };
        control.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        button.AddChild(control);
        var nativeIcon = topBar.Map.GetNode<TextureRect>("Control/Icon");
        var icon = new TextureRect
        {
            Name = "Icon",
            Texture = LoadIcon(),
            Material = (Material)nativeIcon.Material.Duplicate(),
            MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        control.AddChild(icon);
        icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Enlarge the artwork by 5% without changing the shared row's native button spacing.
        var inset = (button.CustomMinimumSize - (button.CustomMinimumSize - new Vector2(8, 8)) * IconSizeMultiplier) / 2;
        icon.OffsetLeft = inset.X;
        icon.OffsetTop = inset.Y;
        icon.OffsetRight = -inset.X;
        icon.OffsetBottom = -inset.Y;
        icon.Resized += () => icon.PivotOffset = icon.Size / 2;

        // Reuse the native deck-count typography, outline, placement and sizing for available points.
        button._points = (MegaLabel)topBar.Deck.GetNode<MegaLabel>("DeckCardCount").Duplicate();
        button._points.Name = "PerkPoints";
        button._points.MouseFilter = MouseFilterEnum.Ignore;
        button.AddChild(button._points);
        return button;
    }

    public override void _Ready()
    {
        // NTopBarButton's initialization path for subclasses; do not call NButton._Ready.
        InitTopBarButton();
        var timer = new Godot.Timer { WaitTime = 0.2, Autostart = true };
        timer.Timeout += Refresh;
        AddChild(timer);
        Refresh();
    }

    private static Texture2D LoadIcon()
    {
        var path = Path.Combine(Path.GetDirectoryName(PerkData.DataDir)!, "ui", "perk-menu-v2.png");
        var image = new Image();
        var result = image.Load(path);
        if (result != Error.Ok) throw new IOException($"could not load Perks icon '{path}': {result}");
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private void Refresh()
    {
        _map = NMapScreen.Instance;
        var host = PerkRuntime.LocalHost();
        Visible = host != null;
        var available = host != null && _map != null && _map.IsOpen && _map.IsVisibleInTree()
            && CombatManager.Instance?.IsInProgress != true;
        SetEnabled(available);
        if (!available && _map != null) PerkTreeScreen.Hide(_map);
        if (_points != null)
        {
            var points = host?.State.UnspentPoints ?? 0;
            _points.Visible = points > 0;
            _points.SetTextAutoSize(points.ToString());
        }
        UpdateScreenOpen();
    }

    protected override bool IsOpen() => _map != null && PerkTreeScreen.IsOpen(_map);

    protected override void OnRelease()
    {
        base.OnRelease();
        Refresh();
        if (IsEnabled && _map != null)
        {
            NHoverTipSet.Remove(this);
            PerkTreeScreen.Toggle(_map);
            UpdateScreenOpen();
        }
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        var points = PerkRuntime.LocalHost()?.State.UnspentPoints ?? 0;
        var tip = PerkHoverTips.PerkTip("menu", "Perks", $"Open your perk tree.\nUnspent points: {points}");
        NHoverTipSet.Remove(this);
        NHoverTipSet.CreateAndShow(this, tip, HoverTipAlignment.Center);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        NHoverTipSet.Remove(this);
    }
}
