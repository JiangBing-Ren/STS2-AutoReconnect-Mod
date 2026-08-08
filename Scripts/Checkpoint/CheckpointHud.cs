using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 对局中常态化检查点浮层（参考 QuickLink 的 QuickLinkMapOverlay / QuickLinkCombatButton /
/// QuickLinkTransitionOverlay 三层 UI 模式）。
///
/// 结构：
///   - CanvasLayer（Layer=130，ProcessMode=Always）常驻挂在 tree.Root，对局中始终可见。
///   - 右下角可拖拽浮动按钮（FAB）：悬停/按下变色 + AnimIn/Out 风格动画；点击展开/收起列表面板。
///   - 检查点列表面板（Panel + ScrollContainer + VBoxContainer）：每个检查点一行按钮，
///     显示“检查点 #seq · 第X幕·第Y层·N人”；点击即回退到该检查点。
///   - 回退过渡遮罩（参考 QuickLinkTransitionOverlay）：点击检查点后黑底淡入 + “正在回退…”文字，
///     场景切换完成后淡出，避免回退过程中的黑屏闪烁。
///
/// 仅在“对局中”（RunManager.Instance.Run != null）显示 FAB。回退由主机/单人执行；
/// 客机点击会提示“仅房主可回退”（与 CheckpointRollback 的 host 判断一致）。
/// </summary>
internal partial class CheckpointHud : CanvasLayer
{
    // ---- 布局常量（参考 QuickLinkMapOverlay 的尺寸分区思路）----
    private const float FabWidth = 132f;
    private const float FabHeight = 48f;
    private const float FabMargin = 24f;
    private const float PanelWidth = 340f;
    private const float PanelHeight = 380f;
    private const float EntryHeight = 60f;
    private const float AnimDuration = 0.16f;
    private const float TransitionHold = 2.6f;

    // 颜色（绿色系，贴合 STS2 的“安全/可回退”语义，参考 QuickLink 的 LabelGreen 系列）
    private static readonly Color ColorNormal = new(0.16f, 0.46f, 0.26f, 0.92f);
    private static readonly Color ColorHover = new(0.28f, 0.82f, 0.42f, 0.98f);
    private static readonly Color ColorPressed = new(0.10f, 0.32f, 0.18f, 0.98f);
    private static readonly Color ColorPanelBg = new(0.08f, 0.10f, 0.12f, 0.94f);
    private static readonly Color ColorBorder = new(0.35f, 0.85f, 0.5f, 1f);

    private Control _root = null!;
    private Button _fab = null!;
    private PanelContainer _panel = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _list = null!;
    private ColorRect _mask = null!;
    private Label _maskLabel = null!;

    private bool _expanded;
    private bool _rollingBack;
    private int _lastCount = -1;
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _fabHome;

    public override void _Ready()
    {
        Layer = 130;
        ProcessMode = ProcessModeEnum.Always;

        _root = new Control();
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_root);

        // ---- 浮动按钮 FAB ----
        _fab = new Button();
        _fab.Text = "检查点";
        _fab.AddThemeFontSizeOverride("font_size", 18);
        _fab.AddThemeColorOverride("font_color", Colors.White);
        _fab.CustomMinimumSize = new Vector2(FabWidth, FabHeight);
        _fab.MouseFilter = Control.MouseFilterEnum.Stop;
        _fab.FocusMode = Control.FocusModeEnum.None;
        ApplyFabStyle(ColorNormal);
        _fab.Pressed += OnFabPressed;
        _fab.GuiInput += OnFabGuiInput;
        _fab.MouseEntered += () => AnimateFabColor(ColorHover);
        _fab.MouseExited += () => { if (!_dragging) AnimateFabColor(_expanded ? ColorHover : ColorNormal); };
        _root.AddChild(_fab);

        // ---- 列表面板 ----
        _panel = new PanelContainer();
        _panel.CustomMinimumSize = new Vector2(PanelWidth, PanelHeight);
        _panel.Visible = false;
        _panel.MouseFilter = Control.MouseFilterEnum.Stop;
        _panel.Modulate = new Color(1, 1, 1, 0);
        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = ColorPanelBg;
        panelStyle.BorderWidthBottom = panelStyle.BorderWidthTop =
            panelStyle.BorderWidthLeft = panelStyle.BorderWidthRight = 2;
        panelStyle.BorderColor = ColorBorder;
        panelStyle.CornerRadiusTopLeft = panelStyle.CornerRadiusTopRight =
            panelStyle.CornerRadiusBottomLeft = panelStyle.CornerRadiusBottomRight = 10;
        _panel.AddThemeStyleboxOverride("panel", panelStyle);
        _root.AddChild(_panel);

        _scroll = new ScrollContainer();
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        _panel.AddChild(_scroll);

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(_list);

        // ---- 回退过渡遮罩（参考 QuickLinkTransitionOverlay）----
        _mask = new ColorRect();
        _mask.Color = new Color(0, 0, 0, 1);
        _mask.Modulate = new Color(1, 1, 1, 0);
        _mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _mask.MouseFilter = Control.MouseFilterEnum.Stop;
        _mask.Visible = false;
        _root.AddChild(_mask);

        _maskLabel = new Label();
        _maskLabel.Text = "正在回退到检查点…";
        _maskLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _maskLabel.VerticalAlignment = VerticalAlignment.Center;
        _maskLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _maskLabel.CustomMinimumSize = new Vector2(720, 96);
        _maskLabel.AddThemeColorOverride("font_color", new Color(1f, 0.94f, 0.72f, 1f));
        _maskLabel.AddThemeFontSizeOverride("font_size", 34);
        _mask.AddChild(_maskLabel);

        // 默认 FAB 位置（右下角），窗口尺寸就绪后计算
        Callable.From(PlaceDefault).CallDeferred();
        Diag.Log("[CheckpointHud] 常驻检查点浮层已就绪（Layer=130）");
    }

    private void PlaceDefault()
    {
        var v = GetViewport().GetVisibleRect().Size;
        _fabHome = new Vector2(v.X - FabWidth - FabMargin, v.Y - FabHeight - FabMargin);
        if (!_dragging) _fab.Position = _fabHome;
        PositionPanelAboveFab();
    }

    public override void _Process(double delta)
    {
        bool inRun = RunManager.Instance?.NetService != null;
        // FAB 仅在对局中（且非回退中）可见
        _fab.Visible = inRun && !_rollingBack;
        if (_expanded) _panel.Visible = inRun && !_rollingBack;
        // 遮罩独立控制（回退中保持）
        _mask.Visible = _rollingBack;
        if (!inRun && !_rollingBack)
        {
            _expanded = false;
            _panel.Visible = false;
        }
        if (inRun) UpdateFabLabel();
        if (_dragging) _fab.Position = GetViewport().GetMousePosition() + _dragOffset;
    }

    private void UpdateFabLabel()
    {
        int n = CheckpointStore.GetEntries().Count;
        _fab.Text = n > 0 ? $"检查点 ({n})" : "检查点";
    }

    // ---------- 按钮样式 / 动画（参考 QuickLink 的 AnimIn/Out、悬停变色）----------

    private void ApplyFabStyle(Color bg)
    {
        var nb = new StyleBoxFlat();
        nb.BgColor = bg;
        nb.BorderWidthBottom = nb.BorderWidthTop = nb.BorderWidthLeft = nb.BorderWidthRight = 2;
        nb.BorderColor = ColorBorder;
        nb.CornerRadiusTopLeft = nb.CornerRadiusTopRight =
            nb.CornerRadiusBottomLeft = nb.CornerRadiusBottomRight = 10;
        _fab.AddThemeStyleboxOverride("normal", nb);
        _fab.AddThemeStyleboxOverride("hover", nb);
        _fab.AddThemeStyleboxOverride("pressed", nb);
        _fab.AddThemeStyleboxOverride("focus", nb);
    }

    private void AnimateFabColor(Color target)
    {
        var t = CreateTween();
        t.TweenProperty(_fab, "modulate", target, AnimDuration);
    }

    // ---------- 拖拽（参考 QuickLinkCombatButton 的可拖拽浮动按钮）----------

    private void OnFabGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _dragging = true;
                _dragOffset = _fab.Position - GetViewport().GetMousePosition();
            }
            else
            {
                _dragging = false;
                _fabHome = _fab.Position; // 松开后停在当前位置
            }
        }
    }

    // ---------- 展开 / 收起面板（Tween 动画）----------

    private void OnFabPressed()
    {
        if (_dragging) { _dragging = false; return; } // 拖拽结束的误触不触发
        if (_rollingBack) return;
        if (_expanded) CollapsePanel();
        else ExpandPanel();
    }

    private void ExpandPanel()
    {
        _expanded = true;
        RebuildList();
        PositionPanelAboveFab();
        _panel.Visible = true;
        _panel.Modulate = new Color(1, 1, 1, 0);
        var t = CreateTween();
        t.TweenProperty(_panel, "modulate", new Color(1, 1, 1, 1), AnimDuration);
        AnimateFabColor(ColorHover);
    }

    private void CollapsePanel()
    {
        _expanded = false;
        AnimateFabColor(ColorNormal);
        var t = CreateTween();
        t.TweenProperty(_panel, "modulate", new Color(1, 1, 1, 0), AnimDuration);
        t.Finished += () => { if (!_expanded) _panel.Visible = false; };
    }

    private void PositionPanelAboveFab()
    {
        float x = _fab.Position.X + (FabWidth - PanelWidth) / 2f;
        float y = _fab.Position.Y - PanelHeight - 10f;
        if (y < 8f) y = _fab.Position.Y + FabHeight + 10f; // FAB 太靠顶则翻到下方
        _panel.Position = new Vector2(x, y);
    }

    // ---------- 检查点列表 ----------

    private void RebuildList()
    {
        foreach (var child in _list.GetChildren())
            _list.RemoveChild(child);

        var entries = CheckpointStore.GetEntries();
        _lastCount = entries.Count;
        // 最新检查点（seq 最大）显示在列表顶部
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (run, label, seq) = entries[i];
            _list.AddChild(MakeEntryButton(run, label, seq));
        }

        if (entries.Count == 0)
        {
            var empty = new Label();
            empty.Text = "暂无检查点\n（推进节点后会自动记录）";
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            empty.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.8f, 1f));
            empty.AddThemeFontSizeOverride("font_size", 16);
            empty.CustomMinimumSize = new Vector2(PanelWidth - 24, EntryHeight * 2);
            _list.AddChild(empty);
        }
    }

    private Button MakeEntryButton(SerializableRun run, string label, int seq)
    {
        var btn = new Button();
        btn.Text = $"检查点 #{seq}\n{label}";
        btn.AddThemeFontSizeOverride("font_size", 15);
        btn.AddThemeColorOverride("font_color", Colors.White);
        btn.CustomMinimumSize = new Vector2(PanelWidth - 24, EntryHeight);
        btn.MouseFilter = Control.MouseFilterEnum.Stop;
        btn.FocusMode = Control.FocusModeEnum.None;
        var nb = new StyleBoxFlat();
        nb.BgColor = new Color(0.14f, 0.30f, 0.20f, 0.95f);
        nb.BorderWidthBottom = nb.BorderWidthTop = nb.BorderWidthLeft = nb.BorderWidthRight = 1;
        nb.BorderColor = new Color(0.3f, 0.7f, 0.45f, 1f);
        nb.CornerRadiusTopLeft = nb.CornerRadiusTopRight =
            nb.CornerRadiusBottomLeft = nb.CornerRadiusBottomRight = 6;
        btn.AddThemeStyleboxOverride("normal", nb);
        btn.AddThemeStyleboxOverride("hover", nb);
        btn.AddThemeStyleboxOverride("pressed", nb);
        btn.Pressed += () => _ = OnEntryChosen(run);
        return btn;
    }

    // ---------- 选择检查点 -> 回退 ----------

    private async Task OnEntryChosen(SerializableRun cp)
    {
        if (_rollingBack) return;
        CollapsePanel();
        if (!IsRollbackAllowed())
        {
            CheckpointRollback.Notify("仅房主可回退", "回退检查点会重新托管整局，只有房主可以执行。请让房主操作，或请房主邀请你重连。");
            return;
        }
        _rollingBack = true;
        await ShowTransition("正在回退到检查点…");
        CheckpointRollback.RollbackTo(cp);
    }

    private bool IsRollbackAllowed()
    {
        var rm = RunManager.Instance;
        if (rm?.NetService == null) return true; // 单人/离线视为可回退
        return rm.NetService.Type == NetGameType.Host;
    }

    // ---------- 回退过渡遮罩（参考 QuickLinkTransitionOverlay.ShowMask/HideMask）----------

    private async Task ShowTransition(string message)
    {
        _maskLabel.Text = message;
        var fadeIn = CreateTween();
        fadeIn.TweenProperty(_mask, "modulate", new Color(1, 1, 1, 1), 0.25f);
        await ToSignal(fadeIn, "finished");
        // 保持遮罩覆盖场景切换（ReturnToMainMenuAfterRun + 推读档界面），然后淡出
        await ToSignal(GetTree().CreateTimer(TransitionHold), "timeout");
        var fadeOut = CreateTween();
        fadeOut.TweenProperty(_mask, "modulate", new Color(1, 1, 1, 0), 0.3f);
        await ToSignal(fadeOut, "finished");
        _rollingBack = false;
        _mask.Visible = false;
        Diag.Log("[CheckpointHud] 回退过渡遮罩已淡出");
    }
}
