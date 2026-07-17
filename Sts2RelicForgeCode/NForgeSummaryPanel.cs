using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.addons.mega_text;                  // MegaLabel (native header label)
using MegaCrit.Sts2.Core.Context;                      // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;             // Player
using MegaCrit.Sts2.Core.Helpers;                      // SceneHelper
using MegaCrit.Sts2.Core.Models;                       // RelicModel
using MegaCrit.Sts2.Core.Nodes;                        // NGame (hover-tips container host)
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;        // NButton, NClickableControl
using MegaCrit.Sts2.Core.Nodes.Relics;                 // NRelicBasicHolder
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;  // NChoiceSelectionSkipButton (native close)
using MegaCrit.Sts2.Core.Runs;                         // RunManager

namespace Sts2RelicForge;

/// <summary>
/// Builds the per-relic text of the forge-summary panel — split out of the node so solo-verify can
/// assert the content without instantiating UI. One row per FORGED relic (companions and unforged
/// relics are skipped): the prefix-decorated name plus the full effect read-out — numeric deltas AND
/// qualitative notes (<see cref="ForgeText.PrefixEffectBody"/>; the old portrait summary showed notes
/// only, which read as "nothing" on a purely numeric forge — the workshop bug report), the curse, and
/// the curse-risk gauge / saturated state.
/// </summary>
internal static class ForgeSummaryRows
{
    internal sealed class Row
    {
        public RelicModel Relic = null!;
        public string Title = "";   // BBCode: tier-colored "<prefix> <relic name>" + curse mark
        public string Body = "";    // BBCode: effect lines + curse + gauge/saturated
    }

    public static List<Row> Build(Player player)
    {
        var rows = new List<Row>();
        foreach (var relic in player.Relics.ToList())
        {
            if (RelicForgeService.IsCompanion(relic)) continue;
            var rec = RelicForgeService.RecordFor(relic);
            if (rec == null) continue;

            // A pickup that ROLLED VANILLA (the ~85% no-prefix outcome) still carries a record, but
            // there is nothing to summarize — listing it as a bare name would drown the real rows.
            bool saturated = RelicForgeService.IsGaugeSaturated(relic);
            if (rec.Prefix.Length == 0 && rec.SelfCurse.Length == 0 && !rec.EnemyRider && !saturated)
                continue;

            string name;
            try { name = relic.Title.GetFormattedText(); }
            catch { name = relic.Id.Entry; }   // a foreign relic without a loc title must not kill the list
            string title = ForgeText.TitlePrefix(rec) + name;
            if (rec.Prefix.Length > 0)
                title = "[color=" + PrefixTable.ColorOf(rec.Prefix) + "]" + title + "[/color]";
            title += ForgeText.TitleSuffix(rec);

            // EFFECT summary only (workshop-owner review): the curse-risk gauge % and its flavor band
            // are forge-location noise here — the ONLY gauge state worth surfacing in the overview is
            // SATURATED (100% = the relic stopped working), which stays as its red one-liner.
            var body = new List<string>();
            string effect = ForgeText.PrefixEffectBody(rec);
            if (effect.Length > 0) body.Add(effect);
            string curse = ForgeText.CurseBody(rec);
            if (curse.Length > 0) body.Add(curse);
            if (saturated) body.Add(ForgeText.SaturatedBody());

            rows.Add(new Row { Relic = relic, Title = title, Body = string.Join("\n", body) });
        }
        return rows;
    }
}

/// <summary>
/// The forge-summary screen: a scrolling list of every forged relic — its icon (a native
/// <see cref="NRelicBasicHolder"/>, so hovering it gives the full vanilla+forge tooltip), its
/// prefix-colored name, and the complete effect / curse / gauge read-out. Opened from the top-bar
/// button (<see cref="NForgeSummaryButton"/>), replacing the old character-portrait tooltip ride —
/// a workshop request: the portrait tip mixed forge info into the (already long) ascension text and
/// hid numeric-only forges entirely.
///
/// Same construction as <see cref="NReforgeRelicPicker"/>: built entirely in code on a top-most
/// CanvasLayer parented to the scene root, dimmed backdrop, ScrollContainer (a variable list must
/// scroll — an unscrolled VBox would grow past the screen), hover-tips container hoisted onto this
/// layer while open. Read-only (issues no commands, mutates no run state) → co-op safe; it lists the
/// LOCAL player's relics (LocalContext.GetMe). Closes on Escape, backdrop click, the Close button,
/// or when the run ends under it (watchdog).
/// </summary>
internal sealed partial class NForgeSummaryPanel : CanvasLayer
{
    private static NForgeSummaryPanel? _open;   // one panel at a time; the button toggles it

    private bool _done;
    private Node? _tipsParent;                  // origin of the hoisted hover-tips container
    private int _tipsIndex = -1;

    /// <summary>Open the panel, or close it if it is already open (the top-bar button is a toggle).</summary>
    public static void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
        var panel = new NForgeSummaryPanel { Layer = 120 };
        _open = panel;
        tree.Root.CallDeferred(Node.MethodName.AddChild, panel); // deferred: root may be busy this frame
    }

    public override void _Ready()
    {
        // Build inside a guard: a throw would leave the opaque backdrop stuck on the persistent root
        // (same failure mode the reforge picker guards against), so tear down on any failure.
        try { BuildUi(); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] forge summary panel build failed: {e}");
            Close();
        }
    }

    /// <summary>Self-close if the run ends (or is left) underneath us — we live on the persistent
    /// scene root, so nothing else would ever free a panel left open across a run teardown.</summary>
    public override void _Process(double delta)
    {
        if (!_done && RunManager.Instance?.State == null) Close();
    }

    private void BuildUi()
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;

        var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f), MouseFilter = Control.MouseFilterEnum.Stop };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true }) Close(); };
        AddChild(bg);

        Label banner = StealBannerLabel() is MegaLabel ml ? ml : new Label();
        if (banner is MegaLabel m) m.AutoSizeEnabled = false;   // auto-size collapses when reparented
        banner.Text = ForgeLoc.Ui("SUMMARY_TITLE");
        banner.HorizontalAlignment = HorizontalAlignment.Center;
        banner.VerticalAlignment = VerticalAlignment.Center;
        banner.AddThemeFontSizeOverride("font_size", 48);
        banner.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        banner.OffsetTop = vp.Y * 0.05f;
        banner.OffsetBottom = vp.Y * 0.13f;
        AddChild(banner);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetLeft = vp.X * 0.18f;
        scroll.OffsetRight = -vp.X * 0.18f;
        scroll.OffsetTop = vp.Y * 0.15f;
        scroll.OffsetBottom = -vp.Y * 0.15f;    // end above the Close button row
        AddChild(scroll);

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 18);
        scroll.AddChild(list);

        var player = LocalContext.GetMe(RunManager.Instance?.State?.Players ?? Enumerable.Empty<Player>());
        var rows = player != null ? ForgeSummaryRows.Build(player) : new List<ForgeSummaryRows.Row>();

        if (rows.Count == 0)
        {
            var empty = new Label { Text = ForgeLoc.Ui("SUMMARY_EMPTY"), HorizontalAlignment = HorizontalAlignment.Center };
            empty.AddThemeFontSizeOverride("font_size", 30);
            empty.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            list.AddChild(empty);
        }

        foreach (var row in rows)
        {
            // Per-relic guard (same as the picker): one foreign relic that can't build its holder must
            // not abort the whole panel — skip it, keep the rest.
            try { list.AddChild(BuildRow(row)); }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}] forge summary: skipped relic {row.Relic?.Id.Entry}: {e.Message}");
            }
        }

        AddCloseButton(vp);
        HoistHoverTips();   // relic-holder tooltips must draw above this layer, not behind it
    }

    private static Control BuildRow(ForgeSummaryRows.Row row)
    {
        var h = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        h.AddThemeConstantOverride("separation", 20);

        var holder = NRelicBasicHolder.Create(row.Relic);
        if (holder != null)
        {
            holder.CustomMinimumSize = new Vector2(90f, 90f);
            holder.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            h.AddChild(holder);
        }

        var texts = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        texts.AddThemeConstantOverride("separation", 4);
        h.AddChild(texts);

        texts.AddChild(Rich(row.Title, 26));
        if (row.Body.Length > 0) texts.AddChild(Rich(row.Body, 20));
        return h;
    }

    /// <summary>A BBCode label that wraps to the row width and sizes its own height. Must be the
    /// game's MegaRichTextLabel, NOT a plain RichTextLabel: the forge bodies use the game's custom
    /// tags ([green]/[red]/…), which are RichTextEffects that only MegaRichTextLabel installs — on a
    /// plain label they render as literal "[green]" text. Auto-size off (it collapses outside the
    /// game's own scenes — same trap as the stolen banner MegaLabel).</summary>
    private static RichTextLabel Rich(string bbcode, int fontSize)
    {
        var l = new MegaRichTextLabel
        {
            AutoSizeEnabled = false,
            BbcodeEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,   // let hovers fall through to the backdrop/holder
            ScrollActive = false,
        };
        l.AddThemeFontSizeOverride("normal_font_size", fontSize);
        l.AddThemeFontSizeOverride("bold_font_size", fontSize);
        l.Text = bbcode;   // set AFTER the flags so the effect install sees the final text
        return l;
    }

    /// <summary>Bottom-center Close: the native skip button if the steal works, else a plain button.</summary>
    private void AddCloseButton(Vector2 vp)
    {
        var row = new CenterContainer();
        row.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        row.OffsetTop = -vp.Y * 0.13f;
        row.OffsetBottom = -vp.Y * 0.03f;
        AddChild(row);

        var native = StealSkipButton();
        if (native != null) { row.AddChild(native); return; }

        var close = new Button { Text = ForgeLoc.Ui("CLOSE") };
        close.AddThemeFontSizeOverride("font_size", 28);
        close.Pressed += Close;
        row.AddChild(close);
    }

    /// <summary>Same steal as the reforge picker: instantiate the relic-selection scene off-tree,
    /// detach its self-contained skip NButton (relabelled "Close"), free the rest.</summary>
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
            MainFile.Logger.Warn($"[{MainFile.ModId}] summary close button load failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Steal the native header MegaLabel (font/style) from the relic-selection scene.</summary>
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
            MainFile.Logger.Warn($"[{MainFile.ModId}] summary banner label load failed: {e.Message}");
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

    /// <summary>Move NGame's hover-tips container into this layer (last child = on top) so relic
    /// tooltips render above the panel; put back exactly on close. Same idiom as the picker.</summary>
    private void HoistHoverTips()
    {
        try
        {
            Node? tips = NGame.Instance?.HoverTipsContainer;
            Node? parent = tips?.GetParent();
            if (tips == null || parent == null) return;
            _tipsParent = parent;
            _tipsIndex = tips.GetIndex();
            parent.RemoveChild(tips);
            AddChild(tips);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary hover-tips hoist failed: {e.Message}"); }
    }

    private void RestoreHoverTips()
    {
        try
        {
            Node? tips = NGame.Instance?.HoverTipsContainer;
            if (tips == null || _tipsParent == null || tips.GetParent() != this) return;
            RemoveChild(tips);
            _tipsParent.AddChild(tips);
            _tipsParent.MoveChild(tips, Mathf.Min(_tipsIndex, _tipsParent.GetChildCount() - 1));
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary hover-tips restore failed: {e.Message}"); }
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
        RestoreHoverTips();   // detach the borrowed tips container BEFORE freeing ourselves
        QueueFree();
    }
}
