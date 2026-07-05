using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;          // MegaLabel
using MegaCrit.Sts2.Core.Assets;               // PreloadManager
using MegaCrit.Sts2.Core.Commands;             // PlayerCmd.LoseGold
using MegaCrit.Sts2.Core.Entities.Players;     // Player
using MegaCrit.Sts2.Core.Helpers;              // TaskHelper, StsColors
using MegaCrit.Sts2.Core.HoverTips;            // HoverTip, IHoverTip
using MegaCrit.Sts2.Core.Localization;         // LocManager
using MegaCrit.Sts2.Core.Models;               // RelicModel
using MegaCrit.Sts2.Core.Nodes.HoverTips;      // NHoverTipSet (game's own tooltip)
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;  // NMerchantInventory
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// A CLEANSE control in the merchant (shop) screen — sibling to <see cref="NMerchantReforgeButton"/>.
/// Pay a fixed gold cost to REMOVE the curse from one of your cursed relics, keeping its prefix (a
/// guaranteed upside — no gamble, unlike reforge). Only relics that currently carry a curse are
/// offered. Sits just to the right of the reforge control. Single-player only (same gate as reforge).
/// </summary>
internal sealed partial class NMerchantCleanseButton : Control
{
    private const float IconSize = 60f;
    private const float HoverScale = 1.2f;
    private const float CostScale = 0.75f;
    private const float CostGap = 6f;
    private const float BelowGap = 12f;
    private const float LeftNudge = 24f;

    private NMerchantInventory _shop = null!;
    private Player? _player;
    private bool _busy;
    private TextureButton _icon = null!;
    private string _tipTitle = "";        // hover-tip title (shown via the game's own NHoverTipSet)
    private string _tipText = "";         // hover-tip body (role when usable, else the disabled reason)
    private Control? _costNode;
    private Tween? _hoverTween;
    private float _contentW = IconSize;
    private float _iconH = IconSize;
    private bool _positioned;

    public static void Attach(NMerchantInventory shop)
    {
        var w = new NMerchantCleanseButton { _shop = shop };
        Node parent = (Node?)shop._slotsContainer ?? shop;
        parent.AddChild(w);
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        Position = new Vector2(200f, 420f);
        _player = _shop.Inventory?.Player ?? RunManager.Instance.State?.Players.FirstOrDefault();

        _icon = new TextureButton
        {
            TextureNormal = LoadCleanseIcon(),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            Size = new Vector2(IconSize, IconSize),
        };
        _icon.Pressed += OnPressed;
        _icon.MouseEntered += () => { ScaleWidget(HoverScale); NHoverTipSet.CreateAndShow(_icon, NMerchantReforgeButton.MakeTip(_tipTitle, _tipText), HoverTipAlignment.Left); };
        _icon.MouseExited += () => { ScaleWidget(1f); NHoverTipSet.Remove(_icon); };
        AddChild(_icon);

        _tipTitle = Localize("정화", "净化", "Cleanse");
        _tipText = Localize("유물에 부여된 저주를 제거합니다.", "移除遗物上的诅咒。", "Remove the relic's curse.");

        BuildCostDisplay();
        LayoutChildren();

        if (_player != null) _player.GoldChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_player != null) _player.GoldChanged -= Refresh;
    }

    public override void _Process(double delta)
    {
        bool open = _shop.IsOpen;
        if (Visible != open) Visible = open;
        // Keep the enabled state live: unlike reforge (always usable), cleanse only lights up when a
        // cursed relic is owned, and that can change while the shop is open — so refresh each frame.
        if (open) { LayoutChildren(); PositionInShop(); Refresh(); }
    }

    private void LayoutChildren()
    {
        float iconW = _icon.Size.X > 0 ? _icon.Size.X : IconSize;
        _iconH = _icon.Size.Y > 0 ? _icon.Size.Y : IconSize;
        float costW = (_costNode?.Size.X ?? 0f) * CostScale;
        float costH = (_costNode?.Size.Y ?? 0f) * CostScale;
        float w = Math.Max(iconW, costW);
        _contentW = w;
        _icon.Position = new Vector2((w - iconW) / 2f, 0f);
        if (_costNode != null)
            _costNode.Position = new Vector2((w - costW) / 2f, _iconH + CostGap);
        PivotOffset = new Vector2(w / 2f, _iconH / 2f);
    }

    private void ScaleWidget(float s)
    {
        _hoverTween?.Kill();
        _hoverTween = CreateTween();
        _hoverTween.TweenProperty(this, "scale", Vector2.One * s, 0.12)
                   .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
    }

    /// <summary>Sit one icon-width to the RIGHT of the reforge control ([row 3, col 1] + offset).</summary>
    private void PositionInShop()
    {
        if (_positioned) return;
        Control? board = _shop._slotsContainer;
        if (board == null) return;

        float minLeft = float.MaxValue, gridBottom = float.MinValue;
        var lefts = new List<float>();
        foreach (var c in new[] { _shop._relicContainer, _shop._potionContainer })
        {
            if (c == null) continue;
            foreach (var slot in c.GetChildren().OfType<NMerchantSlot>())
            {
                Rect2 r = slot.GetGlobalRect();
                if (r.Size.X < 2f) continue;
                minLeft = Math.Min(minLeft, r.Position.X);
                gridBottom = Math.Max(gridBottom, r.End.Y);
                lefts.Add(r.Position.X);
            }
        }
        if (lefts.Count == 0) return;

        // Column pitch = center-to-center spacing between adjacent item columns above. Offset the
        // cleanse control by exactly one pitch to the right of the reforge control, so the two sit
        // spaced like the shop items instead of cramped together.
        lefts.Sort();
        float pitch = IconSize + 20f;   // fallback if only one column is present
        float best = float.MaxValue;
        for (int i = 1; i < lefts.Count; i++)
        {
            float d = lefts[i] - lefts[i - 1];
            if (d > 8f) best = Math.Min(best, d);   // smallest gap between distinct columns = pitch
        }
        if (best < float.MaxValue) pitch = best;

        float iconW = _icon.Size.X > 0 ? _icon.Size.X : IconSize;
        float iconCx = minLeft + iconW / 2f - LeftNudge + pitch;   // one item-column right of reforge
        float iconCy = gridBottom + BelowGap + _iconH / 2f;

        Transform2D inv = board.GetGlobalTransform().AffineInverse();
        Vector2 local = inv * new Vector2(iconCx, iconCy);
        Position = local - new Vector2(_contentW / 2f, _iconH / 2f);
        _positioned = true;
    }

    /// <summary>
    /// The shop cleanse icon — a loose PNG next to the mod DLL (mods/Sts2RelicForge/cleanse_shop_icon.png)
    /// so it can be swapped without a pck rebuild; falls back to the reforge shop icon, then the pck
    /// reforge icon, if it's missing.
    /// </summary>
    private static Texture2D? LoadCleanseIcon()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(typeof(NMerchantCleanseButton).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var name in new[] { "cleanse_shop_icon.png", "reforge_shop_icon.png" })
                {
                    string file = System.IO.Path.Combine(dir, name);
                    if (System.IO.File.Exists(file))
                    {
                        var img = Image.LoadFromFile(file);
                        if (img != null) return ImageTexture.CreateFromImage(img);
                    }
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] shop cleanse icon load failed: {e.Message}"); }
        return PreloadManager.Cache.GetTexture2D("res://images/ui/rest_site/option_reforge.png");
    }

    private void BuildCostDisplay()
    {
        try
        {
            if (_shop._cardRemovalNode?.GetNodeOrNull("Cost") is not Control template) return;
            if (template.Duplicate() is not Control clone) return;
            _costNode = clone;
            clone.Scale = Vector2.One * CostScale;
            AddChild(clone);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] shop cleanse cost display failed: {e.Message}"); }
    }

    private static void SetCostText(Node n, string text, Color color)
    {
        if (n is MegaLabel ml) { ml.SetTextAutoSize(text); ml.Modulate = color; }
        foreach (var c in n.GetChildren()) SetCostText(c, text, color);
    }

    private static string Localize(string ko, string zh, string en)
    {
        string lang = LocManager.Instance?.Language ?? "";
        if (lang.StartsWith("ko")) return ko;
        if (lang.StartsWith("zh")) return zh;
        return en;
    }

    private void Refresh()
    {
        int cost = ForgeConfig.ShopCleanseCost;
        bool affordable = _player != null && _player.Gold >= cost;
        bool hasCursed = _player != null && RestSiteReforgeSupport.HasCleansable(_player);
        bool usable = affordable && hasCursed;

        if (_costNode != null) SetCostText(_costNode, cost.ToString(), affordable ? StsColors.cream : StsColors.red);
        // Gray it out when unusable via modulate only — NOT Disabled, so hover (tooltip) still works
        // and can explain WHY it's disabled. Clicks are guarded in OnPressed.
        _icon.Modulate = usable ? Colors.White : StsColors.halfTransparentWhite;
    }

    private void OnPressed()
    {
        if (_busy || _player == null) return;
        int cost = ForgeConfig.ShopCleanseCost;
        if (_player.Gold < cost) return;
        var candidates = RestSiteReforgeSupport.Cleansable(_player).ToList();
        if (candidates.Count == 0) return;
        _busy = true;
        TaskHelper.RunSafely(Flow(cost, candidates));
    }

    private async Task Flow(int cost, List<RelicModel> candidates)
    {
        try
        {
            RelicModel? chosen = await NReforgeRelicPicker.Show(candidates);
            // Charge only on a real pick that actually had a curse to remove; re-check gold at purchase.
            if (chosen != null && _player != null && _player.Gold >= cost && RelicForgeService.Cleanse(chosen))
            {
                if (cost > 0) await PlayerCmd.LoseGold(cost, _player);
                chosen.Flash();
                MainFile.Logger.Info($"[{MainFile.ModId}] shop cleanse: {chosen.Id.Entry} for {cost}g.");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] shop cleanse failed: {e.Message}"); }
        finally { _busy = false; Refresh(); }
    }
}
