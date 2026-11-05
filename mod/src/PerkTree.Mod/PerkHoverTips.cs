using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace PerkTree.Mod;

/// <summary>
/// Native tooltip rendering/stacking with a screen-space cursor anchor, independent of tree pan/zoom.
/// Stock Godot nodes only: this mod does not register custom Godot scripts.
/// </summary>
internal sealed class PerkHoverTips
{
    private readonly CanvasLayer _layer;
    private readonly Control _surface;
    private Control? _hovered;
    private Control? _anchor;
    private NHoverTipSet? _tips;
    private static readonly HashSet<string> WarnedKeywords = new(StringComparer.Ordinal);

    public PerkHoverTips(CanvasLayer layer, Control surface)
    {
        _layer = layer;
        _surface = surface;
        layer.VisibilityChanged += () => { if (!layer.Visible) Hide(); };
        // Follow the pointer even without a mouse-motion event (window resize, tree movement).
        // Disconnect explicitly: the SceneTree outlives this map screen.
        SceneTree? tree = null;
        // The layer has not been attached yet; connect after entering the scene.
        layer.TreeEntered += () =>
        {
            tree = layer.GetTree();
            tree.ProcessFrame += UpdatePosition;
        };
        layer.TreeExiting += () =>
        {
            if (tree != null) tree.ProcessFrame -= UpdatePosition;
            Hide();
        };
    }

    public static HoverTip PerkTip(string id, string title, string description)
    {
        // HoverTip accepts a localized title plus a raw description. Merge only a namespaced title
        // into the native tooltip table (the same table HoverTipFactory.Static uses).
        // All native entries and their localized text stay intact.
        var key = $"PERKTREE.{id}.title";
        const string table = "static_hover_tips";
        LocManager.Instance.GetTable(table).MergeWith(new Dictionary<string, string> { [key] = title });
        return new HoverTip(new LocString(table, key), description);
    }

    public void Show(Control hovered, HoverTip perk, IEnumerable<string> keywords)
    {
        Hide();
        if (!_layer.Visible || !hovered.IsVisibleInTree()) return;
        _hovered = hovered;
        // Each native CreateAndShow connects TreeExiting on its owner. Give every set its own
        // short-lived anchor so repeated hovers cannot accumulate callbacks on the perk buttons.
        _anchor = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Size = Vector2.One };
        _surface.AddChild(_anchor);
        PositionAnchor();
        var tips = new List<IHoverTip> { perk };
        foreach (var keyword in keywords.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var companion = ResolveKeyword(keyword);
                if (companion != null) tips.Add(companion);
                else WarnKeyword(keyword, "no native tooltip source");
            }
            catch (Exception e) { WarnKeyword(keyword, e.Message); }
        }
        try
        {
            _tips = NHoverTipSet.CreateAndShow(_anchor, tips, Alignment());
            // The tree uses CanvasLayer 60. Put its native tips on that same layer, after the
            // controls, so the game's global hover-tip container cannot render below the overlay.
            if (_tips != null)
            {
                _tips.Reparent(_layer);
                _tips.ProcessMode = Node.ProcessModeEnum.Always;
                _tips.MouseFilter = Control.MouseFilterEnum.Ignore;
                UpdatePosition();
            }
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"could not show perk tooltip: {e}");
            Hide();
        }
    }

    public void Hide()
    {
        if (_tips != null && GodotObject.IsInstanceValid(_tips)) _tips.Visible = false;
        if (_anchor != null)
        {
            NHoverTipSet.Remove(_anchor);
            _anchor.QueueFree();
        }
        _tips = null;
        _anchor = null;
        _hovered = null;
    }

    private void UpdatePosition()
    {
        if (_tips == null || _anchor == null || _hovered == null) return;
        if (!_layer.Visible || !_hovered.IsVisibleInTree()
            || !_hovered.GetGlobalRect().HasPoint(_surface.GetGlobalMousePosition()))
        {
            Hide();
            return;
        }
        PositionAnchor();
        // Native alignment corrects screen overflow and lays long companion sets into columns.
        _tips.SetAlignment(_anchor, Alignment());
    }

    private HoverTipAlignment Alignment() => _surface.GetGlobalMousePosition().X < _surface.GetViewportRect().Size.X / 2
        ? HoverTipAlignment.Right : HoverTipAlignment.Left;

    private void PositionAnchor()
    {
        var offset = Alignment() == HoverTipAlignment.Right ? 22f : -22f;
        _anchor!.GlobalPosition = _surface.GetGlobalMousePosition() + new Vector2(offset, 18);
    }

    private static IHoverTip? ResolveKeyword(string keyword)
    {
        if (Enum.TryParse<CardKeyword>(keyword, out var cardKeyword) && Enum.IsDefined(cardKeyword))
            return HoverTipFactory.FromKeyword(cardKeyword);
        if (Enum.TryParse<StaticHoverTip>(keyword, out var staticTip) && Enum.IsDefined(staticTip))
            return HoverTipFactory.Static(staticTip);
        var power = ModelDb.AllPowers.FirstOrDefault(p => p.GetType().Name == keyword + "Power");
        return power == null ? null : HoverTipFactory.FromPower(power);
    }

    private static void WarnKeyword(string keyword, string reason)
    {
        if (WarnedKeywords.Add(keyword)) MainFile.LogWarn($"perk companion tooltip '{keyword}': {reason}");
    }
}
