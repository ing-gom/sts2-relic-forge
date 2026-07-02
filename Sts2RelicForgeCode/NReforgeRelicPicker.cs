using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions; // NButton, NClickableControl
using MegaCrit.Sts2.Core.Nodes.Relics;          // NRelicBasicHolder

namespace Sts2RelicForge;

/// <summary>
/// Reforge's own relic-selection screen. The game's NChooseARelicSelection lays relics out in a
/// single absolutely-positioned horizontal row (with entrance tweens), so a full inventory spills
/// off-screen and gets clipped. This replacement drops the relics into a GridContainer inside a
/// vertically-scrolling ScrollContainer, so many relics simply wrap onto more rows and scroll.
///
/// Built entirely in code (no scene/pck) on its own top-most CanvasLayer added to the scene root,
/// so it works at a rest site. Returns the chosen relic via Show()'s Task, or null if the player
/// backs out (Skip button or Escape). This path is single-player only (no PlayerChoice net sync);
/// reforge is an SP feature.
/// </summary>
internal sealed partial class NReforgeRelicPicker : CanvasLayer
{
    private IReadOnlyList<RelicModel> _relics = Array.Empty<RelicModel>();
    private readonly TaskCompletionSource<RelicModel?> _tcs = new();
    private bool _done;

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
        var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var banner = new Label
        {
            Text = Localize("재련할 유물 선택", "选择要重铸的遗物", "Choose a relic to reforge"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        banner.AddThemeFontSizeOverride("font_size", 44);
        banner.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        banner.OffsetTop = vp.Y * 0.10f;
        AddChild(banner);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, // wrap instead of scroll sideways
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,       // scroll down when it overflows
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetLeft = vp.X * 0.12f;
        scroll.OffsetRight = -vp.X * 0.12f;
        scroll.OffsetTop = vp.Y * 0.20f;
        scroll.OffsetBottom = -vp.Y * 0.10f;
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

        // Skip button (bottom center) — back out without reforging.
        var skip = new Button { Text = Localize("건너뛰기", "跳过", "Skip") };
        skip.AddThemeFontSizeOverride("font_size", 28);
        AddChild(skip);
        skip.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        skip.OffsetLeft = -90f;
        skip.OffsetRight = 90f;
        skip.OffsetTop = -76f;
        skip.OffsetBottom = -28f;
        skip.Pressed += () => Complete(null);
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
        _tcs.TrySetResult(result);
        QueueFree();
    }

    private static string Localize(string ko, string zh, string en)
    {
        string lang = LocManager.Instance?.Language ?? "";
        if (lang.StartsWith("ko")) return ko;
        if (lang.StartsWith("zh")) return zh;
        return en;
    }
}
