using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;          // MegaLabel
using MegaCrit.Sts2.Core.Assets;               // PreloadManager
using MegaCrit.Sts2.Core.Commands;             // PlayerCmd.LoseGold
using MegaCrit.Sts2.Core.Context;              // LocalContext (resolve the local co-op player)
using MegaCrit.Sts2.Core.Entities.Players;     // Player
using MegaCrit.Sts2.Core.Helpers;              // TaskHelper, StsColors
using MegaCrit.Sts2.Core.HoverTips;            // HoverTip, IHoverTip
using MegaCrit.Sts2.Core.Localization;         // LocManager
using MegaCrit.Sts2.Core.Nodes.HoverTips;      // NHoverTipSet (game's own tooltip)
using MegaCrit.Sts2.Core.Models;               // RelicModel
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;  // NMerchantInventory
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicForge;

/// <summary>
/// A reforge control embedded in the merchant (shop) screen: the mod's reforge icon plus a gold
/// cost shown with the shop's OWN gold icon + cost label (cloned from the card-removal slot's
/// "Cost" node, so it matches vanilla prices exactly). Clicking it pays gold to re-roll the prefix on
/// one of your relics, reusing the same picker + reforge core as the campfire. Uses are unlimited, but
/// the cost ESCALATES within a shop visit (base + step per reforge done — see ForgeConfig), so rerolls
/// are self-limiting; the counter (<see cref="_reforgeCount"/>) lives on this instance and resets at
/// the next shop. Each reforge also fills the relic's own CURSE GAUGE — at 100% it saturates and drops
/// out of the picker (see RelicForgeService.CurseGauge) — and a rolled curse locks that relic to
/// cleanse-only. Gold is charged only once a relic is actually chosen (cancelling the picker is free).
///
/// Single-player only (like the campfire reforge and its picker), gated in the patch below.
/// </summary>
internal sealed partial class NMerchantReforgeButton : Control
{
    private const float IconSize = 60f;        // ~matches a shop relic's on-screen size
    private const float HoverScale = 1.2f;     // enlarge on hover (native slots go 0.65→0.8 ≈ ×1.23)
    private const float CostScale = 0.75f;     // shrink the cloned native price to the shop's on-screen size
    private const float CostGap = 6f;          // vertical gap between icon and cost
    private const float BelowGap = 12f;        // gap below the grid's bottom edge
    private const float LeftNudge = 24f;       // shift left of the grid's left edge

    private NMerchantInventory _shop = null!;
    private Player? _player;
    private bool _busy;
    private int _reforgeCount;      // reforges done in THIS shop visit; each raises the next cost (see ForgeConfig) + indexes the location aura
    private int _locGauge;          // this shop's reforge aura (0–100); fills 5–20% per reforge, ends the visit at 100%
    private bool _ended;            // location aura full — reforging is done at THIS shop this visit (fresh instance resets it)
    private TextureButton _icon = null!;
    private string _tipTitle = "";        // hover-tip title (shown via the game's own NHoverTipSet)
    private string _tipBody = "";         // hover-tip body describing the button's role
    private Control? _costNode;      // the cloned native "Cost" display (gold icon + label)
    private Tween? _hoverTween;
    private float _contentW = IconSize; // widget content width (computed in LayoutChildren)
    private float _contentH = IconSize; // widget content height (computed in LayoutChildren)
    private float _iconH = IconSize;    // icon height (computed in LayoutChildren)
    private bool _positioned;           // position is computed ONCE (immune to item hover-scaling)

    public static void Attach(NMerchantInventory shop)
    {
        var w = new NMerchantReforgeButton { _shop = shop };
        // Sit ON the board (%SlotsContainer) like the native slots, so it slides in with the shop
        // and shares its coordinate space. The board itself isn't scaled by item hovers, so this
        // also keeps us immune to other elements' hover-scaling.
        Node parent = (Node?)shop._slotsContainer ?? shop;
        parent.AddChild(w);
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; // children handle their own input
        Visible = false;                      // only shown while the shop is actually open (see _Process)

        Position = new Vector2(120f, 420f);   // board-local fallback; _Process re-anchors below the grid

        // Resolve the LOCAL player (the one at this client), NOT Players.FirstOrDefault() — in co-op
        // that is the host, so a client's reforge would list/operate on the host's relics instead of
        // its own (LocalContext.GetMe matches by the client's own NetId). Fall back only if unresolved.
        _player = LocalContext.GetMe(RunManager.Instance.State?.Players ?? Enumerable.Empty<Player>())
                  ?? _shop.Inventory?.Player
                  ?? RunManager.Instance.State?.Players.FirstOrDefault();

        // Reforge icon (loaded from the loose PNG), clickable. Hover scales the WHOLE widget (icon +
        // cost) around the icon centre (see LayoutChildren's PivotOffset), so both grow together.
        _icon = new TextureButton
        {
            TextureNormal = LoadShopIcon(),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            Size = new Vector2(IconSize, IconSize),
        };
        _icon.Pressed += OnPressed;
        _icon.MouseEntered += () => { ScaleWidget(HoverScale); NHoverTipSet.CreateAndShow(_icon, MakeTip(_tipTitle, _tipBody), HoverTipAlignment.Left); };
        _icon.MouseExited += () => { ScaleWidget(1f); NHoverTipSet.Remove(_icon); };
        AddChild(_icon);

        _tipTitle = ForgeLoc.Ui("SHOP_REFORGE_TITLE");
        _tipBody = ForgeLoc.Ui("SHOP_REFORGE_BODY");

        BuildCostDisplay();
        LayoutChildren();

        if (_player != null) _player.GoldChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_player != null) _player.GoldChanged -= Refresh;
    }

    // Show only while the merchant screen is actually open (the shop "mat" is up), not the whole
    // time the merchant room exists. IsOpen flips with the shop's Open()/Close().
    public override void _Process(double delta)
    {
        // Per-frame render path: a disposed shop (screen tearing down) would otherwise throw every
        // frame straight into the engine's _Process pump. Contain it so the shop can't crash.
        try
        {
            bool open = _shop.IsOpen;
            if (Visible != open) Visible = open;
            if (open) { LayoutChildren(); PositionInShop(); }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] reforge button _Process failed: {e.Message}"); }
    }

    /// <summary>Center the cost display directly under the icon, and set the widget's pivot to the
    /// icon centre so a hover scale grows icon + cost together around it.</summary>
    private void LayoutChildren()
    {
        float iconW = _icon.Size.X > 0 ? _icon.Size.X : IconSize;
        _iconH = _icon.Size.Y > 0 ? _icon.Size.Y : IconSize;
        float costW = (_costNode?.Size.X ?? 0f) * CostScale; // cost is rendered at CostScale
        float costH = (_costNode?.Size.Y ?? 0f) * CostScale;
        float w = Math.Max(iconW, costW);
        _contentW = w;
        _contentH = _iconH + (_costNode != null ? CostGap + costH : 0f);
        _icon.Position = new Vector2((w - iconW) / 2f, 0f);
        if (_costNode != null)
            _costNode.Position = new Vector2((w - costW) / 2f, _iconH + CostGap);
        PivotOffset = new Vector2(w / 2f, _iconH / 2f);
    }

    /// <summary>Tween the WHOLE widget's scale (icon + cost together), around the icon centre.</summary>
    private void ScaleWidget(float s)
    {
        _hoverTween?.Kill();
        _hoverTween = CreateTween();
        _hoverTween.TweenProperty(this, "scale", Vector2.One * s, 0.12)
                   .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
    }

    private System.Collections.Generic.IEnumerable<Control?> ItemContainers()
    {
        yield return _shop._relicContainer;
        yield return _shop._potionContainer;
        yield return _shop._characterCardContainer;
        yield return _shop._colorlessCardContainer;
    }

    /// <summary>
    /// Place the widget at the "3rd row" of the item grid: the shop shows relics (row 1) and potions
    /// (row 2) in a 3×2 layout, so we drop one more row (same row spacing) under the LEFT column —
    /// the [row 3, col 1] cell. Computed ONCE (cached) from the settled layout, so item hover-scaling
    /// never nudges it. Positions in the board's own coordinate space (child of %SlotsContainer).
    /// </summary>
    private void PositionInShop()
    {
        if (_positioned) return;
        Control? board = _shop._slotsContainer;
        if (board == null) return;

        // Grid bounds (relics + potions), global: left edge + bottom edge.
        float minLeft = float.MaxValue, gridBottom = float.MinValue;
        bool any = false;
        foreach (var c in new[] { _shop._relicContainer, _shop._potionContainer })
        {
            if (c == null) continue;
            foreach (var slot in c.GetChildren().OfType<NMerchantSlot>())
            {
                Rect2 r = slot.GetGlobalRect();
                if (r.Size.X < 2f) continue;
                minLeft = Math.Min(minLeft, r.Position.X);   // left edge of the left column
                gridBottom = Math.Max(gridBottom, r.End.Y);  // bottom edge of the potion row
                any = true;
            }
        }
        if (!any) return; // slots not laid out yet — try again next frame

        float iconW = _icon.Size.X > 0 ? _icon.Size.X : IconSize;
        float iconCx = minLeft + iconW / 2f - LeftNudge;     // just left of the grid's left edge
        float iconCy = gridBottom + BelowGap + _iconH / 2f;  // just under the grid (kept high, on the board)

        Transform2D inv = board.GetGlobalTransform().AffineInverse();
        Vector2 local = inv * new Vector2(iconCx, iconCy);        // board-local icon centre
        Position = local - new Vector2(_contentW / 2f, _iconH / 2f);
        _positioned = true;
    }

    /// <summary>
    /// The shop reforge icon. Loaded from a loose PNG next to the mod DLL
    /// (mods/Sts2RelicForge/reforge_shop_icon.png) so it can be swapped without rebuilding the pck;
    /// falls back to the rest-site reforge icon shipped in the pck if that file is missing.
    /// </summary>
    private static Texture2D? LoadShopIcon()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(typeof(NMerchantReforgeButton).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                string file = System.IO.Path.Combine(dir, "reforge_shop_icon.png");
                if (System.IO.File.Exists(file))
                {
                    var img = Image.LoadFromFile(file);
                    if (img != null) return ImageTexture.CreateFromImage(img);
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] shop reforge icon load failed: {e.Message}"); }
        return PreloadManager.Cache.GetTexture2D("res://images/ui/rest_site/option_reforge.png"); // fallback
    }

    /// <summary>
    /// Clone the shop's own gold cost display (gold icon + cost label) out of the card-removal slot's
    /// "Cost" node, so the price reads exactly like a vanilla shop price. Keep the label reference so
    /// Refresh() can set the amount and recolor it by affordability.
    /// </summary>
    private void BuildCostDisplay()
    {
        try
        {
            if (_shop._cardRemovalNode?.GetNodeOrNull("Cost") is not Control template) return;
            if (template.Duplicate() is not Control clone) return;
            _costNode = clone;
            clone.Scale = Vector2.One * CostScale; // shrink to the shop's on-screen price size
            AddChild(clone); // positioned (centered under the icon) by LayoutChildren
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] shop reforge cost display failed: {e.Message}"); }
    }

    // Set EVERY MegaLabel in the cloned cost display to our reforge cost (the clone was copied while
    // the card-removal showed its own price, so we must overwrite it; setting all labels is robust
    // to however the "Cost" node nests its number label).
    private static void SetCostText(Node n, string text, Color color)
    {
        if (n is MegaLabel ml) { ml.SetTextAutoSize(text); ml.Modulate = color; }
        foreach (var c in n.GetChildren()) SetCostText(c, text, color);
    }

    /// <summary>Build a plain title+body hover tip using the game's own HoverTip (setters are reachable
    /// because ModKit publicizes sts2), so NHoverTipSet renders it exactly like a native tooltip.</summary>
    internal static IHoverTip MakeTip(string title, string body)
    {
        var t = new HoverTip();
        t.Title = title;
        t.Description = body;
        t.Id = "sts2rf_shop_" + title;
        return t;
    }

    /// <summary>Set the cost amount and dim/redden when unaffordable or nothing is reforgeable.</summary>
    private void Refresh()
    {
        int cost = ForgeConfig.ShopReforgeCostFor(_reforgeCount);
        bool affordable = _player != null && _player.Gold >= cost;
        bool hasRelics = _player != null && RestSiteReforgeSupport.HasReforgeable(_player);
        bool usable = affordable && hasRelics && !_ended;

        if (_costNode != null) SetCostText(_costNode, cost.ToString(), affordable ? StsColors.cream : StsColors.red);
        // Gray it out when unusable via modulate only — NOT Disabled, so hover (tooltip) still works.
        // Clicks are guarded in OnPressed, so a grayed button does nothing when pressed.
        _icon.Modulate = usable ? Colors.White : StsColors.halfTransparentWhite;
        // Tooltip shows this shop's location aura fill %, or the ended line once it's full.
        _tipBody = _ended ? ForgeLoc.Ui("SHOP_REFORGE_ENDED")
                 : _locGauge <= 0 ? ForgeLoc.Ui("SHOP_REFORGE_BODY")
                 : string.Format(ForgeLoc.Ui("SHOP_REFORGE_AURA"), _locGauge);
    }

    private void OnPressed()
    {
        if (_busy || _player == null || _ended) return;
        int cost = ForgeConfig.ShopReforgeCostFor(_reforgeCount);
        if (_player.Gold < cost) return;
        var candidates = RestSiteReforgeSupport.Reforgeable(_player).ToList();
        if (candidates.Count == 0) return;
        _busy = true;
        TaskHelper.RunSafely(Flow(cost, candidates));
    }

    private async Task Flow(int cost, List<RelicModel> candidates)
    {
        try
        {
            RelicModel? chosen = await NReforgeRelicPicker.Show(candidates, this);
            // Charge only on a real pick, and re-check gold at purchase time.
            if (chosen != null && _player != null && _player.Gold >= cost)
            {
                if (cost > 0) await PlayerCmd.LoseGold(cost, _player);
                ReforgeNet.Reforge(chosen, _player);         // a curse may roll — the paid gamble
                chosen.Flash();
                // Fill this shop's location aura 5–20% (indexed by reforges done); at 100% reforging ends here.
                _locGauge = Math.Min(RelicForgeService.LocationGaugeFull,
                                     _locGauge + RelicForgeService.LocationGaugeStep(_player, _reforgeCount));
                if (_locGauge >= RelicForgeService.LocationGaugeFull) _ended = true;
                _reforgeCount++;                              // next reforge in this shop costs more (see ForgeConfig)
                MainFile.Logger.Info($"[{MainFile.ModId}] shop reforge #{_reforgeCount} (aura {_locGauge}%): {chosen.Id.Entry} for {cost}g{(_ended ? " [ended]" : "")}.");
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] shop reforge failed: {e.Message}"); }
        finally { _busy = false; Refresh(); }
    }
}

/// <summary>Adds the reforge control to every merchant screen (single-player only).</summary>
[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory._Ready))]
internal static class MerchantReforgeButtonPatch
{
    private static void Postfix(NMerchantInventory __instance)
    {
        try
        {
            // Host: push our forge settings to every client on shop open, so a client that reforges
            // (or buys) here derives the SAME curse the host would — before the button can be clicked.
            ForgeConfigBroadcaster.BroadcastIfHost();

            if (!ReforgeNet.Available()) return; // reforge is SP-only until the networked path lands (ReforgeNet)
            NMerchantReforgeButton.Attach(__instance);
            NMerchantCleanseButton.Attach(__instance);   // sibling: remove a relic's curse, keep the prefix
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] shop reforge button add failed: {e.Message}");
        }
    }
}
