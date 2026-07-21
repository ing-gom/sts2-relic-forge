using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;               // PreloadManager (icon fallback)
using MegaCrit.Sts2.Core.HoverTips;            // HoverTip, HoverTipAlignment
using MegaCrit.Sts2.Core.Nodes.CommonUi;       // NTopBar
using MegaCrit.Sts2.Core.Nodes.HoverTips;      // NHoverTipSet

namespace Sts2RelicForge;

/// <summary>
/// Adds the forge-summary trigger to the top bar's right-hand button cluster (map / deck / pause):
/// a small icon button that toggles <see cref="NForgeSummaryPanel"/> — the workshop-requested
/// standalone home for "what did each forge do to which relic" (previously buried in the character
/// portrait's ascension tooltip). Attached from NTopBar._Ready so it exists for the whole run.
/// </summary>
[HarmonyPatch(typeof(NTopBar), "_Ready")]
internal static class ForgeSummaryButtonPatch
{
    private static void Postfix(NTopBar __instance)
    {
        try { NForgeSummaryButton.Attach(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] forge summary button attach failed: {e.Message}"); }
    }
}

/// <summary>
/// The top-bar toggle button itself. Sits in the SAME parent as the native Deck button: inserted as
/// a sibling in flow order when that parent is a BoxContainer, else manually anchored just LEFT of
/// the leftmost native button (map/deck/pause) — the anchor math runs in _Process until the bar's
/// layout has settled (sizes are zero during _Ready), then freezes. Icon: loose
/// mods/Sts2RelicForge/summary_icon.png (swap without a rebuild), falling back to the shop reforge
/// icon so the button never renders blank. Hover shows a native tooltip; click toggles the panel.
/// Pure local UI — no commands, no run-state writes → co-op safe.
/// </summary>
internal sealed partial class NForgeSummaryButton : TextureButton
{
    private const float Gap = 10f;      // space between us and the leftmost native button
    private const float HoverScale = 1.15f;

    private NTopBar _bar = null!;
    private bool _positioned;           // anchor computed once, after the bar's layout settles
    private bool _inFlow;               // true when a BoxContainer lays us out (no manual anchor)

    public static void Attach(NTopBar bar)
    {
        var deck = (Control?)bar.Deck ?? bar.Map;
        if (deck == null || deck.GetParent() is not Control parent) return;

        // NEVER become a child of the deck button's own wrapper: each native button sits inside a
        // MarginContainer, and a container stacks/positions ALL its children — a sibling added there
        // renders exactly ON TOP of the deck button, and manual GlobalPosition is re-clobbered every
        // layout pass (the first release shipped that overlap). Instead:
        //   • wrapper's parent is a BoxContainer (the button cluster's flow) → insert our own wrapped
        //     button as a SIBLING OF THE WRAPPER, so the flow gives us a real slot left of the deck;
        //   • anything else → parent to NTopBar itself (a plain Control keeps manual positions) and
        //     anchor globally in _Process.
        var target = parent is Container && parent.GetParent() is Control grand ? grand : parent;
        var host = target is BoxContainer ? target : bar;
        if (host.GetNodeOrNull(nameof(NForgeSummaryButton)) != null) return;   // already attached

        var btn = new NForgeSummaryButton { _bar = bar, Name = nameof(NForgeSummaryButton) };
        if (host is BoxContainer box)
        {
            var slot = parent is Container ? parent : deck;   // deck's flow entry (wrapper or itself)
            box.AddChild(btn);
            box.MoveChild(btn, slot.GetIndex());              // sit just before (left of) the deck slot
            btn._inFlow = true;
        }
        else bar.AddChild(btn);
        MainFile.Logger.Info($"[{MainFile.ModId}] summary button attach: host {host.GetType().Name}, "
            + $"deck parent {parent.GetType().Name}, grand {parent.GetParent()?.GetType().Name}, inFlow {btn._inFlow}.");
    }

    public override void _Ready()
    {
        TextureNormal = LoadIcon();
        IgnoreTextureSize = true;
        StretchMode = StretchModeEnum.KeepAspectCentered;

        Pressed += NForgeSummaryPanel.Toggle;
        MouseEntered += () =>
        {
            Scale = Vector2.One * HoverScale;
            ShowTipBelow();
        };
        MouseExited += () =>
        {
            Scale = Vector2.One;
            NHoverTipSet.Remove(this);
        };
    }

    /// <summary>Show the summary tip directly BELOW this icon — like the native relic descriptions, and
    /// unlike the built-in alignments (Left spilled off to the side; Center dropped it below but its
    /// 360px body spilled sideways into the map/deck tips' zone). We skip the game's SetAlignment
    /// (pass None so it does no repositioning) and place the returned set ourselves: down by the icon's
    /// height + a small gap, and — because this icon lives in the top-right button cluster — grown
    /// LEFTWARD when we're on the right half of the screen so the body stays clear of the native cluster
    /// tips and never clips the right edge. _followOwner is off, so _Process leaves our position alone.</summary>
    private void ShowTipBelow()
    {
        var tip = NHoverTipSet.CreateAndShow(this, MakeTip(), HoverTipAlignment.None);
        if (tip == null) return;
        const float tipWidth = 360f;   // NHoverTipSet._hoverTipWidth
        const float gap = 8f;
        float vpWidth = GetViewportRect().Size.X;
        float dx = (GlobalPosition.X > vpWidth * 0.5f) ? (Size.X * Scale.X - tipWidth) : 0f;
        tip.GlobalPosition = GlobalPosition + new Vector2(dx, Size.Y * Scale.Y + gap);
    }

    /// <summary>Match the native buttons' size, and (when not in a BoxContainer flow) anchor just
    /// LEFT of the whole native cluster in GLOBAL coordinates. Local Position is a trap here: the
    /// buttons' local X does not follow their visual order (first release put this button exactly on
    /// top of the deck button), so the anchor is computed from GlobalPosition — layout truth no
    /// matter how the bar nests its children. Runs each frame until the bar's layout yields real
    /// sizes, then freezes — the same settle-then-freeze idiom as the shop reforge button.</summary>
    public override void _Process(double delta)
    {
        if (_positioned) return;
        var cluster = new List<Control>();
        foreach (Control? b in new Control?[] { _bar.Map, _bar.Deck, _bar.Pause })
            if (b != null && b.Size.Y > 1f) cluster.Add(b);
        if (cluster.Count == 0) return;                  // layout not settled yet

        // Size from the SMALLEST native button (the deck control's rect includes its count label,
        // which made a deck-derived side oversized), slightly reduced.
        float side = float.MaxValue;
        foreach (var b in cluster) side = Math.Min(side, b.Size.Y);
        side *= 0.82f;
        CustomMinimumSize = new Vector2(side, side);
        Size = new Vector2(side, side);
        PivotOffset = new Vector2(side / 2f, side / 2f); // hover scale grows around the centre
        if (_inFlow) SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;   // centre in the cluster row

        if (!_inFlow)
        {
            float leftEdge = float.MaxValue, centerY = 0f;
            foreach (var b in cluster)
            {
                leftEdge = Math.Min(leftEdge, b.GlobalPosition.X);
                centerY += b.GlobalPosition.Y + b.Size.Y * b.Scale.Y / 2f;
            }
            centerY /= cluster.Count;
            GlobalPosition = new Vector2(leftEdge - side - Gap, centerY - side / 2f);
            MainFile.Logger.Info($"[{MainFile.ModId}] summary button anchored: side {side:F0}, global ({GlobalPosition.X:F0},{GlobalPosition.Y:F0}), "
                + $"cluster left {leftEdge:F0}, parent {GetParent()?.GetType().Name}.");
        }
        _positioned = true;
    }

    private static IHoverTip MakeTip()
    {
        var t = new HoverTip();   // setters reachable — ModKit publicizes sts2
        t.Title = ForgeLoc.Ui("SUMMARY_TITLE");
        t.Description = ForgeLoc.Ui("SUMMARY_BUTTON_BODY");
        t.Id = "sts2rf_summary_btn";
        return t;
    }

    /// <summary>Loose mods/Sts2RelicForge/summary_icon.png, else the shop reforge loose icon, else
    /// the pck rest-site reforge icon — the button must never render blank.</summary>
    private static Texture2D? LoadIcon()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(typeof(NForgeSummaryButton).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var name in new[] { "summary_icon.png", "reforge_shop_icon.png" })
                {
                    string file = System.IO.Path.Combine(dir, name);
                    if (!System.IO.File.Exists(file)) continue;
                    var img = Image.LoadFromFile(file);
                    if (img != null) return ImageTexture.CreateFromImage(img);
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary icon load failed: {e.Message}"); }
        return PreloadManager.Cache.GetTexture2D("res://images/ui/rest_site/option_reforge.png");
    }
}
