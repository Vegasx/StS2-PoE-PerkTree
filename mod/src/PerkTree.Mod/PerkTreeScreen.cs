using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace PerkTree.Mod;

/// <summary>
/// UI slice 2: the perk tree itself, opened by the map's Perks button. Functional, not pretty: one full-screen overlay on its own
/// CanvasLayer with the tree drawn from the exported layout (node x/y, edges), drag to pan, wheel to zoom, click a node to allocate
/// it, click a pending node to refund it. Committing still happens through the normal map vote (MapPatches), never from here.
/// Built from stock Godot nodes and lambdas only (see PerkMapUi for why there is no Node subclass).
/// </summary>
internal static class PerkTreeScreen
{
    private const string NodeName = "PerkTreeScreen";

    // Node sizes per tier, in tree units (the editor's coordinates). Zoom scales the whole world.
    private static float SizeOf(NodeTier tier) => tier switch { NodeTier.Keystone => 62f, NodeTier.Notable => 44f, _ => 32f };

    // Every node shows its editor colour: dark until a point goes in, full colour once taken. Pending nodes are full colour with a
    // white ring, nodes you can take right now get a light ring, the rest a black one.
    private static readonly Color Locked = new(0.3f, 0.3f, 0.35f);
    private static Color BaseOf(NodeDef n)
    {
        try { return n.Color is { Length: > 0 } c ? Color.FromHtml(c) : new Color(0.79f, 0.8f, 0.84f); }
        catch { return new Color(0.79f, 0.8f, 0.84f); }
    }

    /// <summary>Opens the tree on this map screen (building it the first time), or closes it if it is open.</summary>
    public static void Toggle(NMapScreen map)
    {
        try
        {
            var existing = map.GetNodeOrNull<CanvasLayer>(NodeName);
            if (existing != null) { existing.Visible = !existing.Visible; existing.GetMeta("refresh").As<Callable>().Call(); if (existing.Visible) existing.GetMeta("focus").As<Callable>().Call(); return; }
            var host = PerkRuntime.LocalHost();
            if (host == null) return;
            Build(map, host);
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"could not open the perk tree: {e}");
        }
    }

    /// <summary>Closes the tree if it is open (the map closed, or the player voted).</summary>
    public static void Hide(NMapScreen map)
    {
        var existing = map.GetNodeOrNull<CanvasLayer>(NodeName);
        if (existing != null) existing.Visible = false;
    }

    private static void Build(NMapScreen map, PerkHostModel host)
    {
        var state = host.State;
        var graph = state.Graph;
        var text = PerkText.Load();

        // Always processes, so if the game pauses anyway (pause menu) the Close button keeps working instead of freezing.
        var layer = new CanvasLayer { Name = NodeName, Layer = 60, ProcessMode = Node.ProcessModeEnum.Always };

        // Background: catches every click so nothing reaches the map (no accidental vote while the tree is open), and does pan/zoom.
        // It also holds keyboard focus while the tree is open: Esc then reaches bg.GuiInput, which closes the tree and accepts the
        // event, before the game's own Esc handling (NHotkeyManager/NInputManager use _UnhandledInput) can open the pause menu.
        var bg = new ColorRect { Color = new Color(0.05f, 0.06f, 0.08f, 0.96f), MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(bg);

        // Everything in tree coordinates lives under "world"; panning moves it, zooming scales it.
        var world = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        bg.AddChild(world);

        // ---- lines (drawn first, so nodes sit on top). Uses the editor's own lines and sockets when the tree file has them, so the
        // picture matches the editor; older files fall back to the rule graph's straight edges.
        var tree = graph.Tree;
        var socketPos = tree.Sockets.ToDictionary(sk => sk.Id, sk => new Vector2((float)sk.X, (float)sk.Y));
        Vector2 PosOf(string id) => socketPos.TryGetValue(id, out var p) ? p : new Vector2((float)graph.Node(id).X, (float)graph.Node(id).Y);
        bool IsSocket(string id) => socketPos.ContainsKey(id);

        var drawn = new List<(string a, string b, List<Vector2>? pts)>();
        if (tree.Lines.Count > 0)
            foreach (var l in tree.Lines)
            {
                if (!(IsSocket(l.A) || graph.TryGetNode(l.A, out _)) || !(IsSocket(l.B) || graph.TryGetNode(l.B, out _))) continue;
                drawn.Add((l.A, l.B, l.Pts?.Where(pt => pt.Length >= 2).Select(pt => new Vector2((float)pt[0], (float)pt[1])).ToList()));
            }
        else
        {
            var seen = new HashSet<(string, string)>();
            foreach (var node in graph.Nodes)
                foreach (var other in graph.Neighbors(node.Id))
                    if (seen.Add(string.CompareOrdinal(node.Id, other) < 0 ? (node.Id, other) : (other, node.Id))) drawn.Add((node.Id, other, null));
        }

        // Perk nodes each socket joins (through chains of sockets), for colouring the lines that pass through it.
        var socketLinks = socketPos.Keys.ToDictionary(k => k, _ => new HashSet<string>());
        var adjacency = new Dictionary<string, List<string>>();
        foreach (var (a, b, _) in drawn) { (adjacency.TryGetValue(a, out var la) ? la : adjacency[a] = new()).Add(b); (adjacency.TryGetValue(b, out var lb) ? lb : adjacency[b] = new()).Add(a); }
        foreach (var sk in socketPos.Keys)
        {
            var stack = new Stack<string>(); var visited = new HashSet<string> { sk }; stack.Push(sk);
            while (stack.Count > 0)
                foreach (var nb in adjacency.TryGetValue(stack.Pop(), out var l) ? l : new List<string>())
                    if (visited.Add(nb)) { if (IsSocket(nb)) stack.Push(nb); else socketLinks[sk].Add(nb); }
        }

        var edgeLines = new List<(Line2D line, string a, string b)>();
        foreach (var (a, b, pts) in drawn)
        {
            var line = new Line2D { Width = 5, DefaultColor = Locked, JointMode = Line2D.LineJointMode.Round };
            if (pts is { Count: >= 2 }) foreach (var pt in pts) line.AddPoint(pt);
            else { line.AddPoint(PosOf(a)); line.AddPoint(PosOf(b)); }
            world.AddChild(line);
            edgeLines.Add((line, a, b));
        }

        // Sockets: small dark joints, as in the editor. Not clickable.
        foreach (var (id, pos) in socketPos)
        {
            var dot = new Panel { Size = new Vector2(16, 16), Position = pos - new Vector2(8, 8), MouseFilter = Control.MouseFilterEnum.Ignore };
            var dotStyle = new StyleBoxFlat { BgColor = new Color(0.22f, 0.23f, 0.27f) };
            dotStyle.SetCornerRadiusAll(8);
            dot.AddThemeStyleboxOverride("panel", dotStyle);
            world.AddChild(dot);
        }

        // ---- top bar: status, info about the hovered node, buttons
        var bar = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        var barBox = new VBoxContainer();
        var row = new HBoxContainer();
        var status = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var refundAll = new Button { Text = "Refund pending", FocusMode = Control.FocusModeEnum.None };
        var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None };
        row.AddChild(status);
        row.AddChild(refundAll);
        row.AddChild(close);
        var info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 48) };
        barBox.AddChild(row);
        barBox.AddChild(info);
        var pad = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) pad.AddThemeConstantOverride($"margin_{side}", 10);
        pad.AddChild(barBox);
        bar.AddChild(pad);
        layer.AddChild(bar);

        // ---- nodes: a coin icon (art/perk-tokens-v1, rim tinted with the node colour by branch_tint.gdshader) under an invisible
        // button that takes the clicks and draws a state ring. Perks without an icon get a plain coloured disc.
        var coins = new Dictionary<string, (TextureRect? coin, ShaderMaterial? mat, StyleBoxFlat ring, StyleBoxFlat? disc)>();
        foreach (var node in graph.Nodes)
        {
            var size = SizeOf(node.Tier);
            var ringPad = 5f;   // room for the state ring around the coin
            var ring = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false };
            ring.SetCornerRadiusAll((int)(size / 2 + ringPad));
            ring.SetBorderWidthAll(3);
            var button = new Button
            {
                Size = new Vector2(size + ringPad * 2, size + ringPad * 2),
                Position = new Vector2((float)node.X - size / 2 - ringPad, (float)node.Y - size / 2 - ringPad),
                FocusMode = Control.FocusModeEnum.None,
                Flat = true,
            };
            foreach (var st in new[] { "normal", "pressed", "hover", "disabled", "focus" }) button.AddThemeStyleboxOverride(st, ring);

            TextureRect? coin = null; ShaderMaterial? mat = null; StyleBoxFlat? disc = null;
            var tex = PerkIcons.Get(text.For(graph.EffectOf(node).Id).Icon);
            if (tex != null)
            {
                mat = PerkIcons.Material();
                coin = new TextureRect
                {
                    Texture = tex, Material = mat, MouseFilter = Control.MouseFilterEnum.Ignore,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    Position = new Vector2(ringPad, ringPad), Size = new Vector2(size, size),
                };
                button.AddChild(coin);
            }
            else
            {
                disc = new StyleBoxFlat { BgColor = Locked };
                disc.SetCornerRadiusAll((int)(size / 2));
                disc.SetBorderWidthAll(node.Tier == NodeTier.Small ? 2 : 4);
                disc.BorderColor = new Color(0, 0, 0);
                var discPanel = new Panel { Position = new Vector2(ringPad, ringPad), Size = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
                discPanel.AddThemeStyleboxOverride("panel", disc);
                button.AddChild(discPanel);
            }
            coins[node.Id] = (coin, mat, ring, disc);

            var id = node.Id;
            button.MouseEntered += () => info.Text = Describe(graph, state, text, id);
            button.Pressed += () =>
            {
                var result = state.IsAllocated(id) ? state.TryRefundPending(id) : state.TryAllocate(id);
                if (result != PerkOpResult.Ok) info.Text = $"{Describe(graph, state, text, id)}\n[{Explain(result)}]";
                else info.Text = Describe(graph, state, text, id);
                Refresh();
            };
            world.AddChild(button);
        }

        // ---- pan / zoom
        var zoom = 0.6f;
        var pan = Vector2.Zero;
        void ApplyView() { world.Scale = new Vector2(zoom, zoom); world.Position = pan; }

        // Fit the whole tree on screen, centred, below the top bar. Done after the layer is in the scene (FitView is called
        // below), because before that the viewport size is not known and the first open used to land far off to the bottom right.
        void FitView()
        {
            var xs = graph.Nodes.Select(n => (float)n.X).Concat(socketPos.Values.Select(v => v.X)).ToList();
            var ys = graph.Nodes.Select(n => (float)n.Y).Concat(socketPos.Values.Select(v => v.Y)).ToList();
            if (xs.Count == 0) return;
            var min = new Vector2(xs.Min(), ys.Min()); var max = new Vector2(xs.Max(), ys.Max());
            var view = bg.GetViewport().GetVisibleRect().Size;
            const float top = 110f;   // the info bar
            var span = (max - min) + new Vector2(120, 120);
            zoom = Math.Clamp(Math.Min(view.X / span.X, (view.Y - top) / span.Y), 0.15f, 1.2f);
            var centre = (min + max) / 2;
            pan = new Vector2(view.X / 2, top + (view.Y - top) / 2) - centre * zoom;
            ApplyView();
        }

        bg.GuiInput += ev =>
        {
            if (ev is InputEventKey { Pressed: true, Keycode: Key.Escape } || ev.IsActionPressed("ui_cancel"))
            {
                layer.Visible = false;
                bg.AcceptEvent();
                return;
            }
            switch (ev)
            {
                case InputEventMouseButton { Pressed: true } click when click.ButtonIndex == MouseButton.Left && !bg.HasFocus():
                    bg.GrabFocus();
                    break;
                case InputEventMouseMotion motion when (motion.ButtonMask & (MouseButtonMask.Left | MouseButtonMask.Right | MouseButtonMask.Middle)) != 0:
                    pan += motion.Relative;
                    ApplyView();
                    break;
                case InputEventMouseButton { Pressed: true } wheel when wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                    // Zoom around the cursor: the tree point under the mouse stays under the mouse.
                    var before = (wheel.Position - pan) / zoom;
                    zoom = Math.Clamp(zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f), 0.15f, 2.5f);
                    pan = wheel.Position - before * zoom;
                    ApplyView();
                    break;
            }
        };

        refundAll.Pressed += () => { state.RefundAllPending(); Refresh(); };
        close.Pressed += () => layer.Visible = false;

        // ---- colours and status from the current state
        void Refresh()
        {
            foreach (var node in graph.Nodes)
            {
                var baseColour = BaseOf(node);
                var taken = state.Committed.Contains(node.Id);
                var pending = state.Pending.Contains(node.Id);
                var canTake = !taken && !pending && state.CanAllocate(node.Id) == PerkOpResult.Ok;
                // Untaken coins are shown darkened (whole coin, through the material's vertex colour); taken and pending at full colour.
                var shade = taken || pending ? new Color(1, 1, 1) : canTake ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.4f);
                var (coin, mat, ring, disc) = coins[node.Id];
                mat?.SetShaderParameter("branch_color", baseColour);
                if (coin != null) coin.SelfModulate = shade;
                if (disc != null) disc.BgColor = baseColour * shade;
                ring.BorderColor = pending ? new Color(1, 1, 1) : canTake ? new Color(0.8f, 0.8f, 0.8f, 0.8f) : new Color(0, 0, 0, 0);
            }
            // A perk end is lit when the perk is allocated; a socket end when it joins at least two allocated perks (the path goes through it).
            bool On(string id) => IsSocket(id) ? socketLinks[id].Count(state.IsAllocated) >= 2 : state.IsAllocated(id);
            Color ColourOf(string a, string b)
            {
                var perk = !IsSocket(a) ? a : !IsSocket(b) ? b : socketLinks[a].FirstOrDefault();
                return perk != null ? BaseOf(graph.Node(perk)) : Locked;
            }
            foreach (var (line, a, b) in edgeLines)
            {
                var edgeColour = ColourOf(a, b);
                var aOn = On(a); var bOn = On(b);
                line.DefaultColor = aOn && bOn ? edgeColour : aOn || bOn ? edgeColour.Darkened(0.45f) : edgeColour.Darkened(0.7f);
            }
            status.Text = $"Points: {state.UnspentPoints}    Taken: {state.Committed.Count}    Pending: {state.Pending.Count}    "
                + (state.AllocationOpen ? "Allocation open (locks when you pick your next map node)" : "Allocation closed until the next map");
            refundAll.Disabled = state.Pending.Count == 0 || !state.AllocationOpen;
        }

        layer.SetMeta("refresh", Callable.From(Refresh));
        layer.SetMeta("focus", Callable.From(() => bg.GrabFocus()));
        map.AddChild(layer);
        FitView();
        Refresh();
        bg.GrabFocus();
        info.Text = "Drag to pan, mouse wheel to zoom. Click a node to take it, click a green (pending) node to give it back.";
    }

    private static string Describe(TreeGraph graph, PerkState state, PerkText text, string nodeId)
    {
        var node = graph.Node(nodeId);
        var effect = graph.EffectOf(node);
        var entry = text.For(effect.Id);
        var chance = (node.Chance ?? effect.Chance) * 100m;
        var amount = entry.Percent ? node.Magnitude * 100m : node.Magnitude;
        var body = entry.Text
            .Replace("{v}", Fmt(amount)).Replace("{u}", Fmt(amount)).Replace("{c}", Fmt(chance))
            .Replace("{d}", Fmt(amount * 2)).Replace("{h}", Fmt(amount / 2)).Replace("{m}", "1");
        var sb = new StringBuilder();
        sb.Append($"{entry.Name}  ({node.Tier})");
        if (chance < 100 && !entry.Text.Contains("{c}")) sb.Append($"  {Fmt(chance)}% chance");
        if (state.Committed.Contains(nodeId)) sb.Append("  [taken]");
        else if (state.Pending.Contains(nodeId)) sb.Append("  [pending]");
        sb.Append('\n').Append(body);
        return sb.ToString();
    }

    private static string Fmt(decimal d) => d == Math.Round(d) ? ((int)d).ToString() : d.ToString("0.##");

    private static string Explain(PerkOpResult r) => r switch
    {
        PerkOpResult.WindowClosed => "Allocation is closed until the next map screen",
        PerkOpResult.NoPoints => "No points left",
        PerkOpResult.NotConnected => "Must connect to a node you already have",
        PerkOpResult.WouldDisconnect => "Other pending nodes depend on this one",
        PerkOpResult.NotPending => "Already locked in",
        _ => r.ToString(),
    };
}

/// <summary>Display text exported by tools/export-mod.mjs as data/text.json. Missing file or entry falls back to the effect id.</summary>
internal sealed class PerkText
{
    internal sealed record Entry(string Name, string Text, bool Percent, string? Icon);

    private readonly Dictionary<string, Entry> _entries;
    private static PerkText? _loaded;

    private PerkText(Dictionary<string, Entry> entries) => _entries = entries;

    public Entry For(string effectId) => _entries.TryGetValue(effectId, out var e) ? e : new Entry(effectId, effectId, false, null);

    public static PerkText Load()
    {
        if (_loaded != null) return _loaded;
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(PerkData.DataDir, "text.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                entries[p.Name] = new Entry(
                    v.TryGetProperty("name", out var n) ? n.GetString() ?? p.Name : p.Name,
                    v.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    v.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.True,
                    v.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null);
            }
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"perk text not loaded, showing ids instead: {e.Message}");
        }
        return _loaded = new PerkText(entries);
    }
}

/// <summary>
/// The coin icons (art/perk-tokens-v1, png-128) and the branch tint shader, loaded from the mod folder's icons/ directory. Loaded
/// straight from the PNG files (no .import step: the mod has no pck), each once. Missing files just mean no icon.
/// </summary>
internal static class PerkIcons
{
    private static readonly Dictionary<string, Texture2D?> Cache = new(StringComparer.Ordinal);
    private static Shader? _shader;
    private static bool _shaderTried;

    private static string Dir => Path.Combine(Path.GetDirectoryName(PerkData.DataDir) ?? ".", "icons");

    public static Texture2D? Get(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        if (Cache.TryGetValue(icon, out var hit)) return hit;
        Texture2D? tex = null;
        try
        {
            var path = Path.Combine(Dir, icon + ".png");
            if (File.Exists(path))
            {
                var img = new Image();
                if (img.Load(path) == Error.Ok) tex = ImageTexture.CreateFromImage(img);
            }
        }
        catch (Exception e) { MainFile.LogWarn($"icon {icon} not loaded: {e.Message}"); }
        return Cache[icon] = tex;
    }

    /// <summary>A fresh material per coin, because each node sets its own branch_color (a shared material would share it).</summary>
    public static ShaderMaterial? Material()
    {
        if (!_shaderTried)
        {
            _shaderTried = true;
            try
            {
                var path = Path.Combine(Dir, "branch_tint.gdshader");
                if (File.Exists(path)) _shader = new Shader { Code = File.ReadAllText(path) };
            }
            catch (Exception e) { MainFile.LogWarn($"tint shader not loaded: {e.Message}"); }
        }
        return _shader == null ? null : new ShaderMaterial { Shader = _shader };
    }
}
