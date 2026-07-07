using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;           // MegaLabel (native header label)
using MegaCrit.Sts2.Core.Helpers;               // SceneHelper (relic-screen scene path)
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;                 // NGame (hover-tips container host)
using MegaCrit.Sts2.Core.Nodes.GodotExtensions; // NButton, NClickableControl
using MegaCrit.Sts2.Core.Nodes.Relics;          // NRelicBasicHolder
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection; // NChoiceSelectionSkipButton (native skip)

namespace Sts2RelicForge;

/// <summary>
/// Reforge's own relic-selection screen. The game's NChooseARelicSelection lays relics out in a
/// single absolutely-positioned horizontal row (with entrance tweens), so a full inventory spills
/// off-screen and gets clipped. This replacement drops the relics into a GridContainer inside a
/// vertically-scrolling ScrollContainer, so many relics simply wrap onto more rows and scroll.
///
/// Built entirely in code (no scene/pck) on its own top-most CanvasLayer added to the scene root,
/// so it reliably renders over everything at a rest site or shop. Returns the chosen relic via
/// Show()'s Task, or null if the player backs out (Skip / Escape). This path is single-player only
/// (no PlayerChoice net sync); reforge is an SP feature.
///
/// Hover tooltips are added to NGame's HoverTipsContainer, which lives on the game's own (lower)
/// canvas — so on our top layer they'd render BEHIND our panel. While the picker is open we hoist
/// that container into THIS CanvasLayer (as our last child, above our grid) and restore it on
/// close, so a hovered relic's tooltip draws on top. See <see cref="HoistHoverTips"/>.
/// </summary>
internal sealed partial class NReforgeRelicPicker : CanvasLayer
{
    private IReadOnlyList<RelicModel> _relics = Array.Empty<RelicModel>();
    private readonly TaskCompletionSource<RelicModel?> _tcs = new();
    private bool _done;

    // Origin of the hoisted hover-tips container, so we can put it back exactly on close.
    private Node? _tipsParent;
    private int _tipsIndex = -1;

    public static Task<RelicModel?> Show(IReadOnlyList<RelicModel> relics)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return Task.FromResult<RelicModel?>(null);
        var picker = new NReforgeRelicPicker { _relics = relics, Layer = 120 };
        tree.Root.CallDeferred(Node.MethodName.AddChild, picker); // deferred: root may be busy this frame
        return picker._tcs.Task;
    }

    public override void _Ready()
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;

        // Dimmed backdrop; MouseFilter.Stop so clicks don't fall through to the rest site behind.
        var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f), MouseFilter = Control.MouseFilterEnum.Stop };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        // Steal the game's native skip button (self-contained NButton — works reliably reparented).
        var nativeSkip = StealSkipButton();

        // Header: reuse the native MegaLabel the relic-selection screen uses (same font/style).
        // MegaLabel auto-sizes its font to its rect, which collapsed to the tiny minimum when
        // reparented — that was the blurry/invisible text. So turn auto-size OFF and set an explicit
        // font size. Fall back to a plain Label if the steal fails; either way it always shows.
        Label banner = StealBannerLabel() is MegaLabel ml ? ml : new Label();
        if (banner is MegaLabel m) m.AutoSizeEnabled = false;
        banner.Text = ForgeLoc.Ui("PICKER_BANNER_REFORGE");
        banner.HorizontalAlignment = HorizontalAlignment.Center;
        banner.AddThemeFontSizeOverride("font_size", 48);
        banner.VerticalAlignment = VerticalAlignment.Center;
        banner.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        banner.OffsetTop = vp.Y * 0.15f;    // a proper band, lower down, text vertically centered in it
        banner.OffsetBottom = vp.Y * 0.23f;
        AddChild(banner);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, // wrap instead of scroll sideways
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,       // scroll down when it overflows
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetLeft = vp.X * 0.12f;
        scroll.OffsetRight = -vp.X * 0.12f;
        scroll.OffsetTop = vp.Y * 0.25f;    // below the (lowered) banner
        scroll.OffsetBottom = -vp.Y * 0.18f; // end above the skip button (its row sits at ~84–95%)
        AddChild(scroll);

        // Column count from available width so the grid never needs to scroll sideways.
        int cols = Mathf.Clamp((int)(vp.X * 0.76f / 120f), 2, 8);
        var grid = new GridContainer { Columns = cols };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 16);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(grid);

        foreach (var relic in _relics)
        {
            var holder = NRelicBasicHolder.Create(relic);
            if (holder == null) continue;
            holder.CustomMinimumSize = new Vector2(100f, 100f); // uniform cells; icon renders inside
            var captured = relic;
            holder.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Select(captured)));
            grid.AddChild(holder);
        }

        AddSkipButton(vp, nativeSkip);

        HoistHoverTips(); // move relic tooltips into this layer so they draw above the panel
    }

    /// <summary>
    /// Move NGame's hover-tips container into this CanvasLayer (as our last child, so tooltips draw
    /// above our backdrop/grid), remembering where it came from. Relic tooltips are added to that
    /// same container, so while it lives under us they render on top of the picker instead of behind.
    /// </summary>
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
            AddChild(tips); // appended last -> on top of everything else on this layer
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] hover-tips hoist failed: {e.Message}"); }
    }

    /// <summary>Put the hover-tips container back where it was, BEFORE we free ourselves (else it
    /// would be freed along with the picker).</summary>
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
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] hover-tips restore failed: {e.Message}"); }
    }

    /// <summary>Bottom-center Skip: the native skip button if we got one, else a plain button.
    /// A CenterContainer keeps whichever we use at its natural size.</summary>
    private void AddSkipButton(Vector2 vp, NChoiceSelectionSkipButton? native)
    {
        var row = new CenterContainer();
        row.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        row.OffsetTop = -vp.Y * 0.16f;
        row.OffsetBottom = -vp.Y * 0.05f;
        AddChild(row);

        if (native != null) { row.AddChild(native); return; }

        var skip = new Button { Text = ForgeLoc.Ui("SKIP") };
        skip.AddThemeFontSizeOverride("font_size", 28);
        skip.Pressed += () => Complete(null);
        row.AddChild(skip);
    }

    /// <summary>
    /// Instantiate the game's relic-selection scene ONCE (never added to the tree, so its _Ready
    /// never runs) and steal its native NChoiceSelectionSkipButton — a self-contained NButton that
    /// works correctly reparented. It's detached and returned (its own _Ready runs once we add it
    /// under us); the rest of the scene is freed. Null on any failure (a plain button is used then).
    /// </summary>
    private NChoiceSelectionSkipButton? StealSkipButton()
    {
        try
        {
            var packed = GD.Load<PackedScene>(SceneHelper.GetScenePath("screens/choose_a_relic_selection_screen"));
            Node? screen = packed?.Instantiate();
            if (screen == null) return null;

            var skip = screen.GetNodeOrNull<NChoiceSelectionSkipButton>("SkipButton");
            skip?.GetParent()?.RemoveChild(skip); // detach before freeing the throwaway screen
            screen.Free();

            if (skip != null)
            {
                skip._optionName = ForgeLoc.Ui("SKIP"); // label text, read in its _Ready
                skip.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Complete(null)));
            }
            return skip;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] native skip button load failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Steal the native header MegaLabel out of the relic-selection screen (for its font/style) and
    /// reparent it. Instantiates the scene without adding it to the tree, finds the first Label under
    /// the "Banner" node, detaches it, and frees the rest. Null on failure (a plain Label is used).
    /// </summary>
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
            MainFile.Logger.Warn($"[{MainFile.ModId}] native banner label load failed: {e.Message}");
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
            Complete(null); // Escape = cancel (no visible Skip button)
            GetViewport().SetInputAsHandled();
        }
    }

    private void Select(RelicModel relic) => Complete(relic);

    private void Complete(RelicModel? result)
    {
        if (_done) return;
        _done = true;
        RestoreHoverTips(); // detach the borrowed tips container before we free ourselves
        _tcs.TrySetResult(result);
        QueueFree();
    }

}
