using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.addons.mega_text;                  // MegaLabel (native header label)
using MegaCrit.Sts2.Core.Helpers;                      // SceneHelper
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;        // NButton, NClickableControl
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;  // NChoiceSelectionSkipButton (native close)

namespace Sts2RelicForge;

/// <summary>
/// The CUSTOM pool editor (workshop request): every rollable prefix and every curse as an individual
/// enable/disable checkbox, applied while 'Prefix pool' is set to Custom. Opened from the mod's
/// ModConfig row (a Button entry) — the framework's settings tab can't nest a dynamic sub-list, so
/// the editing surface is this standalone CanvasLayer (same skeleton as the forge-summary panel).
///
/// Entries are grouped into TABS — Enhance (pure numeric tiers) / Effects (mechanic-adding,
/// universal) / Character (character-gated affixes) / Curses (the combined self-curse namespace) —
/// using the same derivations as the pool filter itself (IsEnhance, RequiredCharacter), so the tab
/// a prefix appears under always matches how the filter would classify it.
///
/// Edits write straight into <see cref="CustomPool"/> (saved per toggle), and every change
/// re-broadcasts rf_config so co-op clients converge immediately. A guard keeps at least ONE prefix
/// enabled across the three prefix tabs (an empty prefix pool has no meaning — Roll would fall
/// back); curses CAN all be disabled, which legitimately means "no self-curses roll".
/// </summary>
internal sealed partial class NCustomPoolPanel : CanvasLayer
{
    private static NCustomPoolPanel? _open;

    private bool _done;
    private Label? _hint;                                       // standing hint; flashes warnings
    private readonly List<(CheckBox box, string name)> _prefixRows = new();
    private readonly List<(CheckBox box, string key)> _curseRows = new();
    private readonly List<Button> _tabButtons = new();
    private readonly List<Control> _tabContents = new();

    public static void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
        var panel = new NCustomPoolPanel { Layer = 125 };       // above the settings screen
        _open = panel;
        tree.Root.CallDeferred(Node.MethodName.AddChild, panel);
    }

    public override void _Ready()
    {
        try { BuildUi(); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] custom pool panel build failed: {e}");
            Close();
        }
    }

    private void BuildUi()
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;

        var bg = new ColorRect { Color = new Color(0.02f, 0.02f, 0.03f, 0.97f), MouseFilter = Control.MouseFilterEnum.Stop };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true }) Close(); };
        AddChild(bg);

        Label banner = StealBannerLabel() is MegaLabel ml ? ml : new Label();
        if (banner is MegaLabel m) m.AutoSizeEnabled = false;
        banner.Text = ForgeLoc.Ui("CUSTOM_TITLE");
        banner.HorizontalAlignment = HorizontalAlignment.Center;
        banner.VerticalAlignment = VerticalAlignment.Center;
        banner.AddThemeFontSizeOverride("font_size", 42);
        banner.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        banner.OffsetTop = vp.Y * 0.025f;
        banner.OffsetBottom = vp.Y * 0.095f;
        AddChild(banner);

        _hint = new Label
        {
            Text = ForgeLoc.Ui("CUSTOM_HINT"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(0.85f, 0.8f, 0.65f),
        };
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _hint.OffsetTop = vp.Y * 0.10f;
        _hint.OffsetBottom = vp.Y * 0.13f;
        AddChild(_hint);

        // ----- tab definitions: same derivations as the pool filter (IsEnhance / character gate) -----
        var rollable = PrefixTable.Pool.Where(p => !p.Penalty && !p.IsFallback).ToList();
        bool IsChar(Prefix p) => p.CharAffix || p.RequiredCharacter.Length > 0;
        var tabs = new (string title, Action<VBoxContainer> fill)[]
        {
            (ForgeLoc.Ui("CUSTOM_TAB_ENHANCE"),
                v => FillPrefixTab(v, rollable.Where(p => p.IsEnhance))),
            (ForgeLoc.Ui("CUSTOM_TAB_EFFECTS"),
                v => FillPrefixTab(v, rollable.Where(p => !p.IsEnhance && !IsChar(p)))),
            (ForgeLoc.Ui("CUSTOM_TAB_CHARACTER"),
                v => FillPrefixTab(v, rollable.Where(p => !p.IsEnhance && IsChar(p)))),
            (ForgeLoc.Ui("CUSTOM_CURSES"), FillCurseTab),
        };

        // Tab bar (manual buttons — full control over the selected state, matches the code-built UI).
        var tabBar = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabBar.AddThemeConstantOverride("separation", 12);
        tabBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        tabBar.OffsetTop = vp.Y * 0.135f;
        tabBar.OffsetBottom = vp.Y * 0.185f;
        AddChild(tabBar);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetLeft = vp.X * 0.14f;
        scroll.OffsetRight = -vp.X * 0.14f;
        scroll.OffsetTop = vp.Y * 0.20f;
        scroll.OffsetBottom = -vp.Y * 0.13f;
        AddChild(scroll);

        var host = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(host);

        for (int i = 0; i < tabs.Length; i++)
        {
            var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = i == 0 };
            content.AddThemeConstantOverride("separation", 6);
            tabs[i].fill(content);
            host.AddChild(content);
            _tabContents.Add(content);

            int idx = i;
            var btn = new Button { Text = tabs[i].title, ToggleMode = false };
            btn.AddThemeFontSizeOverride("font_size", 22);
            btn.CustomMinimumSize = new Vector2(150f, 0f);
            btn.Pressed += () => SelectTab(idx);
            tabBar.AddChild(btn);
            _tabButtons.Add(btn);
        }
        SelectTab(0);

        AddCloseButton(vp);
    }

    /// <summary>Show one tab's rows (and highlight its button). Internal so the self-test can drive it.</summary>
    internal void SelectTab(int idx)
    {
        for (int i = 0; i < _tabContents.Count; i++)
        {
            _tabContents[i].Visible = i == idx;
            _tabButtons[i].Modulate = i == idx ? new Color(1f, 0.9f, 0.55f) : new Color(0.75f, 0.75f, 0.75f);
        }
    }

    // ---- tab content ------------------------------------------------------------------------------

    private void FillPrefixTab(VBoxContainer v, IEnumerable<Prefix> prefixes)
    {
        var list = prefixes.OrderBy(p => p.Display, StringComparer.Ordinal).ToList();
        v.AddChild(BulkRow(
            enableAll: () => SetPrefixes(list, disabled: false),
            disableAll: () => SetPrefixes(list, disabled: true)));
        foreach (var p in list)
        {
            string note = p.IsEnhance
                ? (p.Amplify ? "×" : (p.PowerPct >= 0 ? "+" : "") + $"{p.PowerPct:P0}")
                : p.NoteDisplay;
            var captured = p;
            var row = Row(p.Display, p.Color, note,
                enabled: !CustomPool.DisabledPrefixes.Contains(p.Name),
                onToggled: on => OnPrefixToggled(captured.Name, on),
                out var box);
            _prefixRows.Add((box, p.Name));
            v.AddChild(row);
        }
    }

    private void FillCurseTab(VBoxContainer v)
    {
        var penalties = PrefixTable.All.Where(p => p.Penalty && !p.IsFallback).ToList();
        v.AddChild(BulkRow(
            enableAll: () => SetCurses(disabled: false),
            disableAll: () => SetCurses(disabled: true)));
        foreach (var c in SelfCurseTable.Pool)
        {
            var captured = c;
            var row = Row(c.Display, c.Color, c.Effect,
                enabled: !CustomPool.DisabledCurses.Contains(c.En),
                onToggled: on => OnCurseToggled(captured.En, on),
                out var box);
            _curseRows.Add((box, c.En));
            v.AddChild(row);
        }
        foreach (var p in penalties)
        {
            var captured = p;
            var row = Row(p.Display, p.Color, p.NoteDisplay,
                enabled: !CustomPool.DisabledCurses.Contains(p.Name),
                onToggled: on => OnCurseToggled(captured.Name, on),
                out var box);
            _curseRows.Add((box, p.Name));
            v.AddChild(row);
        }
    }

    private Control BulkRow(Action enableAll, Action disableAll)
    {
        var h = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.End };
        h.AddThemeConstantOverride("separation", 12);
        h.AddChild(SmallButton(ForgeLoc.Ui("ENABLE_ALL"), enableAll));
        h.AddChild(SmallButton(ForgeLoc.Ui("DISABLE_ALL"), disableAll));
        var wrap = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        wrap.AddThemeConstantOverride("separation", 4);
        wrap.AddChild(h);
        wrap.AddChild(new HSeparator());
        return wrap;
    }

    private static Button SmallButton(string text, Action onPressed)
    {
        var b = new Button { Text = text };
        b.AddThemeFontSizeOverride("font_size", 18);
        b.Pressed += () => onPressed();
        return b;
    }

    private static Control Row(string name, string colorHex, string note, bool enabled, Action<bool> onToggled, out CheckBox box)
    {
        var h = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        h.AddThemeConstantOverride("separation", 14);

        box = new CheckBox { ButtonPressed = enabled };
        box.Toggled += on => onToggled(on);
        h.AddChild(box);

        var nameLabel = new Label { Text = name, CustomMinimumSize = new Vector2(230f, 0f) };
        nameLabel.AddThemeFontSizeOverride("font_size", 21);
        try { nameLabel.Modulate = Color.FromHtml(colorHex.TrimStart('#')); } catch { /* keep default */ }
        h.AddChild(nameLabel);

        var noteLabel = new Label
        {
            Text = note.Replace("\n", " · "),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        noteLabel.AddThemeFontSizeOverride("font_size", 17);
        noteLabel.Modulate = new Color(0.78f, 0.75f, 0.68f);
        h.AddChild(noteLabel);
        return h;
    }

    // ---- state ------------------------------------------------------------------------------------

    private void OnPrefixToggled(string name, bool enabled)
    {
        if (!enabled && EnabledPrefixCount() <= 1 && !CustomPool.DisabledPrefixes.Contains(name))
        {
            // Refuse to disable the LAST enabled prefix: revert the box and flash the warning.
            foreach (var (box, n) in _prefixRows)
                if (n == name) box.SetPressedNoSignal(true);
            if (_hint != null) _hint.Text = ForgeLoc.Ui("CUSTOM_LAST_PREFIX");
            return;
        }
        if (enabled) CustomPool.DisabledPrefixes.Remove(name);
        else CustomPool.DisabledPrefixes.Add(name);
        Commit();
    }

    private void OnCurseToggled(string key, bool enabled)
    {
        if (enabled) CustomPool.DisabledCurses.Remove(key);
        else CustomPool.DisabledCurses.Add(key);
        Commit();
    }

    private int EnabledPrefixCount()
        => _prefixRows.Count(r => !CustomPool.DisabledPrefixes.Contains(r.name));

    /// <summary>Bulk-set ONE TAB's prefixes. Disabling keeps the global ≥1 invariant: if the whole
    /// pool would empty, the first row of this tab stays enabled (with the warning flashed).</summary>
    private void SetPrefixes(List<Prefix> tab, bool disabled)
    {
        foreach (var p in tab)
        {
            if (disabled) CustomPool.DisabledPrefixes.Add(p.Name);
            else CustomPool.DisabledPrefixes.Remove(p.Name);
        }
        if (disabled && EnabledPrefixCount() == 0 && tab.Count > 0)
        {
            CustomPool.DisabledPrefixes.Remove(tab[0].Name);
            if (_hint != null) _hint.Text = ForgeLoc.Ui("CUSTOM_LAST_PREFIX");
        }
        RefreshBoxes();
        Commit();
    }

    private void SetCurses(bool disabled)
    {
        foreach (var (_, key) in _curseRows)
        {
            if (disabled) CustomPool.DisabledCurses.Add(key);
            else CustomPool.DisabledCurses.Remove(key);
        }
        RefreshBoxes();
        Commit();
    }

    private void RefreshBoxes()
    {
        foreach (var (box, name) in _prefixRows) box.SetPressedNoSignal(!CustomPool.DisabledPrefixes.Contains(name));
        foreach (var (box, key) in _curseRows) box.SetPressedNoSignal(!CustomPool.DisabledCurses.Contains(key));
    }

    private void Commit()
    {
        CustomPool.Save();
        ForgeConfigBroadcaster.BroadcastIfHost();   // co-op: clients converge on the new sets at once
    }

    // ---- chrome (same steals as the other panels) -------------------------------------------------

    private void AddCloseButton(Vector2 vp)
    {
        var row = new CenterContainer();
        row.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        row.OffsetTop = -vp.Y * 0.115f;
        row.OffsetBottom = -vp.Y * 0.025f;
        AddChild(row);

        var native = StealSkipButton();
        if (native != null) { row.AddChild(native); return; }

        var close = new Button { Text = ForgeLoc.Ui("CLOSE") };
        close.AddThemeFontSizeOverride("font_size", 26);
        close.Pressed += Close;
        row.AddChild(close);
    }

    private NChoiceSelectionSkipButton? StealSkipButton()
    {
        try
        {
            var packed = GD.Load<PackedScene>(SceneHelper.GetScenePath("screens/choose_a_relic_selection_screen"));
            Node? screen = packed?.Instantiate();
            if (screen == null) return null;
            var skip = screen.GetNodeOrNull<NChoiceSelectionSkipButton>("SkipButton");
            skip?.GetParent()?.RemoveChild(skip);
            screen.Free();
            if (skip != null)
            {
                skip._optionName = ForgeLoc.Ui("CLOSE");
                skip.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Close()));
            }
            return skip;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] custom pool close button load failed: {e.Message}");
            return null;
        }
    }

    private Label? StealBannerLabel()
    {
        try
        {
            var packed = GD.Load<PackedScene>(SceneHelper.GetScenePath("screens/choose_a_relic_selection_screen"));
            Node? screen = packed?.Instantiate();
            Node? bannerNode = screen?.GetNodeOrNull("Banner");
            Label? label = bannerNode != null ? FindLabel(bannerNode) : null;
            label?.GetParent()?.RemoveChild(label);
            screen?.Free();
            return label;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] custom pool banner label load failed: {e.Message}");
            return null;
        }
    }

    private static Label? FindLabel(Node n)
    {
        if (n is Label l) return l;
        foreach (var c in n.GetChildren())
            if (FindLabel(c) is Label found) return found;
        return null;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Close()
    {
        if (_done) return;
        _done = true;
        if (_open == this) _open = null;
        CustomPool.Save();
        QueueFree();
    }
}
