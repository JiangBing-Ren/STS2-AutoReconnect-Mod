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
///     显示“检查点 #seq ·（当前位置）”、节点类型·坐标、第X幕·第Y层·N人·金币·卡牌数。
///     最新（=当前所在）检查点灰显禁用，避免“回退到当前点”的无意义操作。
///   - 回退二次确认框：点击非当前检查点后弹出，确认才执行（防误触，多人回退代价大）。
///   - 回退过渡遮罩（参考 QuickLinkTransitionOverlay）：黑底淡入 + “正在回退…”文字，
///     场景切换完成后淡出，避免回退过程中的闪屏。
///
/// 交互细节（v0.8.10-min）：
///   - 浮动按钮：左键点击展开/收起列表面板；右键按住拖动按钮位置。
///   - 点击面板/确认框以外的区域自动收起列表。
///   - 快捷键 F5 开关列表面板。
///   - 面板打开时若捕获到新检查点，列表实时刷新（当前位置标记自动前移）。
///
/// 仅在“对局中”（RunManager.Instance.NetService != null）显示 FAB。回退由主机/单人执行；
/// 客机点击会提示“仅房主可回退”（与 CheckpointRollback 的 host 判断一致）。
/// </summary>
internal partial class CheckpointHud : CanvasLayer
{
    // ---- 布局常量（参考 QuickLinkMapOverlay 的尺寸分区思路）----
    private const float FabWidth = 132f;
    private const float FabHeight = 48f;
    private const float FabMargin = 24f;
    private const float PanelWidth = 360f;
    private const float PanelHeight = 400f;
    private const float EntryHeight = 70f;
    private const float AnimDuration = 0.16f;

    // 颜色（绿色系，贴合 STS2 的“安全/可回退”语义，参考 QuickLink 的 LabelGreen 系列）
    private static readonly Color ColorNormal = new(0.16f, 0.46f, 0.26f, 0.92f);
    private static readonly Color ColorHover = new(0.28f, 0.82f, 0.42f, 0.98f);
    private static readonly Color ColorPressed = new(0.10f, 0.32f, 0.18f, 0.98f);
    private static readonly Color ColorPanelBg = new(0.08f, 0.10f, 0.12f, 0.94f);
    private static readonly Color ColorBorder = new(0.35f, 0.85f, 0.5f, 1f);
    private static readonly Color ColorWarn = new(0.95f, 0.78f, 0.30f, 1f); // 确认框警示黄

    private Control _root = null!;
    private Control _outside = null!;
    private Button _fab = null!;
    private PanelContainer _panel = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _list = null!;
    private ColorRect _mask = null!;
    private Label _maskLabel = null!;
    private PanelContainer _confirm = null!;
    private Label _confirmTitle = null!;
    private Label _confirmMsg = null!;

    private bool _expanded;
    private bool _maskShown; // 遮罩是否处于“应显示”状态，由 CheckpointRollback.IsRollingBack 驱动
    private bool _confirming;
    private int _lastCount = -1;
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _fabHome;
    private SerializableRun? _pendingCheckpoint;

    public override void _Ready()
    {
        Layer = 130;
        ProcessMode = ProcessModeEnum.Always;

        _root = new Control();
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_root);

        // ---- 点击外部收起用的全屏捕获层（置于最底层，FAB/面板在其之上）----
        _outside = new Control();
        _outside.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _outside.MouseFilter = Control.MouseFilterEnum.Stop;
        _outside.Visible = false;
        _outside.GuiInput += OnOutsideInput;
        _root.AddChild(_outside);

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
        SetCornerRadius(panelStyle, 10);
        _panel.AddThemeStyleboxOverride("panel", panelStyle);
        _root.AddChild(_panel);

        _scroll = new ScrollContainer();
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        _panel.AddChild(_scroll);

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 6);
        // 列表横向撑满 ScrollContainer（横向滚动禁用时，撑满即可避免按钮左侧贴边、右侧留空）。
        _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _list.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scroll.AddChild(_list);

        // ---- 回退二次确认框（置于面板之上、遮罩之下）----
        BuildConfirmPanel();

        // ---- 回退过渡遮罩（参考 QuickLinkTransitionOverlay，置于最顶层）----
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

    private void BuildConfirmPanel()
    {
        _confirm = new PanelContainer();
        _confirm.CustomMinimumSize = new Vector2(440, 210);
        _confirm.Visible = false;
        _confirm.MouseFilter = Control.MouseFilterEnum.Stop;
        _confirm.Modulate = new Color(1, 1, 1, 0);
        var cStyle = new StyleBoxFlat();
        cStyle.BgColor = new Color(0.08f, 0.10f, 0.12f, 0.97f);
        cStyle.BorderWidthBottom = cStyle.BorderWidthTop =
            cStyle.BorderWidthLeft = cStyle.BorderWidthRight = 2;
        cStyle.BorderColor = ColorWarn;
        SetCornerRadius(cStyle, 12);
        _confirm.AddThemeStyleboxOverride("panel", cStyle);
        _root.AddChild(_confirm);

        var cv = new VBoxContainer();
        cv.AddThemeConstantOverride("separation", 14);
        _confirm.AddChild(cv);

        _confirmTitle = new Label();
        _confirmTitle.AddThemeFontSizeOverride("font_size", 20);
        _confirmTitle.AddThemeColorOverride("font_color", Colors.White);
        _confirmTitle.HorizontalAlignment = HorizontalAlignment.Center;
        cv.AddChild(_confirmTitle);

        _confirmMsg = new Label();
        _confirmMsg.AddThemeFontSizeOverride("font_size", 15);
        _confirmMsg.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f, 1f));
        _confirmMsg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _confirmMsg.HorizontalAlignment = HorizontalAlignment.Center;
        cv.AddChild(_confirmMsg);

        var hb = new HBoxContainer();
        hb.Alignment = BoxContainer.AlignmentMode.Center;
        hb.AddThemeConstantOverride("separation", 16);
        cv.AddChild(hb);

        var okBtn = new Button();
        okBtn.Text = "确定回退";
        okBtn.AddThemeFontSizeOverride("font_size", 16);
        okBtn.CustomMinimumSize = new Vector2(150, 46);
        okBtn.FocusMode = Control.FocusModeEnum.None;
        var okStyle = new StyleBoxFlat();
        okStyle.BgColor = new Color(0.55f, 0.22f, 0.22f, 1f);
        okStyle.BorderWidthBottom = okStyle.BorderWidthTop =
            okStyle.BorderWidthLeft = okStyle.BorderWidthRight = 2;
        okStyle.BorderColor = new Color(0.95f, 0.55f, 0.55f, 1f);
        SetCornerRadius(okStyle, 8);
        ApplyButtonStyle(okBtn, okStyle);
        okBtn.Pressed += OnConfirmRollback;
        hb.AddChild(okBtn);

        var cancelBtn = new Button();
        cancelBtn.Text = "取消";
        cancelBtn.AddThemeFontSizeOverride("font_size", 16);
        cancelBtn.CustomMinimumSize = new Vector2(150, 46);
        cancelBtn.FocusMode = Control.FocusModeEnum.None;
        var cancelStyle = new StyleBoxFlat();
        cancelStyle.BgColor = new Color(0.20f, 0.25f, 0.30f, 1f);
        cancelStyle.BorderWidthBottom = cancelStyle.BorderWidthTop =
            cancelStyle.BorderWidthLeft = cancelStyle.BorderWidthRight = 2;
        cancelStyle.BorderColor = new Color(0.5f, 0.6f, 0.7f, 1f);
        SetCornerRadius(cancelStyle, 8);
        ApplyButtonStyle(cancelBtn, cancelStyle);
        cancelBtn.Pressed += OnCancelRollback;
        hb.AddChild(cancelBtn);
    }

    private static void SetCornerRadius(StyleBoxFlat s, float r)
    {
        int ri = (int)r;
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight =
            s.CornerRadiusBottomLeft = s.CornerRadiusBottomRight = ri;
    }

    private static void ApplyButtonStyle(Button b, StyleBoxFlat s)
    {
        b.AddThemeStyleboxOverride("normal", s);
        b.AddThemeStyleboxOverride("hover", s);
        b.AddThemeStyleboxOverride("pressed", s);
        b.AddThemeStyleboxOverride("focus", s);
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
        // “在对局中”用 IsInProgress（= State != null）判断，而非 NetService != null——
        // 主界面 / 好友大厅等场景 NetService 已就绪但 State 为 null，应隐藏浮层。
        bool inRun = RunManager.Instance?.IsInProgress ?? false;
        bool showUi = inRun && !CheckpointRollback.IsRollingBack;
        // FAB 仅在对局中（且非回退中）可见
        _fab.Visible = showUi;
        if (_expanded) _panel.Visible = showUi;
        // 回退遮罩统一由 CheckpointRollback.IsRollingBack 驱动（HUD 面板与掉线弹窗两条路径都经 RollbackTo），
        // 单人回退不叠手画遮罩（DoRollback 内部用原生 NGame.Transition 遮罩，避免双重黑屏）。
        UpdateRollbackMask();
        // 点击外部捕获层：仅列表展开且未回退/未确认时启用
        _outside.Visible = _expanded && !CheckpointRollback.IsRollingBack && !_confirming;

        if (!inRun && !CheckpointRollback.IsRollingBack)
        {
            _expanded = false;
            _panel.Visible = false;
            _confirming = false;
            _confirm.Visible = false;
        }

        if (inRun)
        {
            UpdateFabLabel();
            // 实时刷新：打开状态下若检查点数量变化（捕获了新检查点），重建列表
            if (_expanded && !CheckpointRollback.IsRollingBack && !_confirming)
            {
                int n = CheckpointStore.GetEntries().Count;
                if (n != _lastCount) RebuildList();
            }
        }

        if (_dragging) _fab.Position = GetViewport().GetMousePosition() + _dragOffset;
        if (_confirming)
        {
            var vs = GetViewport().GetVisibleRect().Size;
            _confirm.Position = (vs - _confirm.CustomMinimumSize) / 2f;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // 快捷键 F5：对局中开关列表面板（确认/回退中不响应）
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F5)
        {
            if (RunManager.Instance?.NetService != null && !CheckpointRollback.IsRollingBack && !_confirming)
            {
                if (_expanded) CollapsePanel();
                else ExpandPanel();
            }
        }
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
        SetCornerRadius(nb, 10);
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
        if (@event is InputEventMouseButton mb)
        {
            // 右键按住拖动浮动按钮；左键仅用于展开/收起面板。
            if (mb.ButtonIndex == MouseButton.Right)
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
    }

    private void OnOutsideInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            CollapsePanel();
    }

    // ---------- 展开 / 收起面板（Tween 动画）----------

    private void OnFabPressed()
    {
        if (_dragging) { _dragging = false; return; } // 拖拽结束的误触不触发
        if (CheckpointRollback.IsRollingBack || _confirming) return;
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
        // 面板贴在 FAB 上方，水平方向与 FAB 右缘对齐（FAB 在右下角，面板向左延展），
        // 保持浮层与浮动按钮的视觉关联，而非居中于视口。
        var v = GetViewport().GetVisibleRect().Size;
        float x = _fab.Position.X + FabWidth - PanelWidth;
        x = Mathf.Clamp(x, 8f, v.X - PanelWidth - 8f);
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
        // 最新检查点（seq 最大、i == Count-1）即“当前所在”，灰显禁用。
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (run, label, seq) = entries[i];
            bool isCurrent = (i == entries.Count - 1);
            _list.AddChild(MakeEntryButton(run, label, seq, isCurrent));
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
            empty.CustomMinimumSize = new Vector2(PanelWidth - 48, EntryHeight * 2);
            empty.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _list.AddChild(empty);
        }
    }

    private Button MakeEntryButton(SerializableRun run, string label, int seq, bool isCurrent)
    {
        var btn = new Button();
        btn.Text = ""; // 文本交给子 Label，确保多行+水平/垂直都能真正居中
        // 不拉满：固定略窄宽度（左右各留空隙），用 ShrinkCenter 让按钮整块在面板内水平居中。
        btn.CustomMinimumSize = new Vector2(PanelWidth - 48, EntryHeight);
        btn.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        btn.MouseFilter = Control.MouseFilterEnum.Stop;
        btn.FocusMode = Control.FocusModeEnum.None;

        string title = isCurrent ? $"检查点 #{seq}  ·  当前位置" : $"检查点 #{seq}";
        var text = new Label();
        text.Text = $"{title}\n{label}";
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddThemeFontSizeOverride("font_size", 14);
        text.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        text.MouseFilter = Control.MouseFilterEnum.Ignore;
        btn.AddChild(text);

        var nb = new StyleBoxFlat();
        nb.BorderWidthBottom = nb.BorderWidthTop = nb.BorderWidthLeft = nb.BorderWidthRight = 1;
        SetCornerRadius(nb, 6);

        if (isCurrent)
        {
            // 当前位置：灰显，禁用回退
            nb.BgColor = new Color(0.20f, 0.22f, 0.24f, 0.92f);
            nb.BorderColor = new Color(0.5f, 0.55f, 0.6f, 1f);
            text.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.8f, 1f));
            btn.Disabled = true;
        }
        else
        {
            nb.BgColor = new Color(0.14f, 0.30f, 0.20f, 0.95f);
            nb.BorderColor = new Color(0.3f, 0.7f, 0.45f, 1f);
            text.AddThemeColorOverride("font_color", Colors.White);
            btn.Pressed += () => _ = OnEntryChosen(run);
        }
        btn.AddThemeStyleboxOverride("normal", nb);
        btn.AddThemeStyleboxOverride("hover", nb);
        btn.AddThemeStyleboxOverride("pressed", nb);
        return btn;
    }

    // ---------- 选择检查点 -> 二次确认 -> 回退 ----------

    private async Task OnEntryChosen(SerializableRun cp)
    {
        if (CheckpointRollback.IsRollingBack || _confirming) return;
        if (!IsRollbackAllowed())
        {
            CheckpointRollback.Notify("仅房主可回退", "回退检查点会重新托管整局，只有房主可以执行。请让房主操作，或请房主邀请你重连。");
            return;
        }
        _pendingCheckpoint = cp;
        ShowConfirm();
    }

    private void ShowConfirm()
    {
        _confirming = true;
        _confirmTitle.Text = "回退到检查点？";
        _confirmMsg.Text = IsHost()
            ? "回退将重新托管整局：所有玩家会短暂回到主菜单，然后一起进入所选检查点。\n确定继续？"
            : "确定回退到该检查点？当前进度将丢失（之后仍可再次回退到更早的检查点）。";
        _confirm.Visible = true;
        _confirm.Modulate = new Color(1, 1, 1, 0);
        var t = CreateTween();
        t.TweenProperty(_confirm, "modulate", new Color(1, 1, 1, 1), 0.12f);
    }

    private void HideConfirm()
    {
        _confirming = false;
        var t = CreateTween();
        t.TweenProperty(_confirm, "modulate", new Color(1, 1, 1, 0), 0.12f);
        t.Finished += () => { if (!_confirming) _confirm.Visible = false; };
    }

    private async void OnConfirmRollback()
    {
        if (CheckpointRollback.IsRollingBack || _pendingCheckpoint == null) return;
        var target = _pendingCheckpoint;
        HideConfirm();
        _expanded = false;
        _panel.Visible = false;
        // 回退标志与遮罩均由 CheckpointRollback.RollbackTo 内部统一置位/复位，
        // _Process 中的 UpdateRollbackMask 据此淡入/淡出手画遮罩（与掉线弹窗路径完全一致）。
        await CheckpointRollback.RollbackTo(target);
        _pendingCheckpoint = null;
    }

    private void OnCancelRollback()
    {
        // 取消：保留列表展开，方便重新选择
        HideConfirm();
    }

    private bool IsRollbackAllowed()
    {
        var rm = RunManager.Instance;
        if (rm?.NetService == null) return true; // 主菜单（理论上 HUD 仅对局中显示）视为可回退
        // 单人（Singleplayer）或主机（Host）均可回退；客机（Client）不可重新托管整局。
        return rm.NetService.Type == NetGameType.Host
            || rm.NetService.Type == NetGameType.Singleplayer;
    }

    private bool IsHost()
    {
        var rm = RunManager.Instance;
        return rm?.NetService?.Type == NetGameType.Host;
    }

    private bool IsSingleplayer()
    {
        var rm = RunManager.Instance;
        return rm?.NetService == null || rm.NetService.Type == NetGameType.Singleplayer;
    }

    // ---------- 回退过渡遮罩（参考 QuickLinkTransitionOverlay，由 CheckpointRollback.IsRollingBack 统一驱动）----------

    private void UpdateRollbackMask()
    {
        // 单一真相：CheckpointRollback.IsRollingBack（HUD 面板触发与掉线弹窗触发都经 RollbackTo 设置）。
        // 单人/离线回退不显示手画遮罩（DoRollback 内部用原生 NGame.Transition 遮罩，避免双重黑屏）。
        bool wantMask = CheckpointRollback.IsRollingBack && !CheckpointRollback.IsRollingBackSingleplayer;
        if (wantMask == _maskShown) return;
        _maskShown = wantMask;
        _mask.Visible = true;
        if (wantMask) _mask.Modulate = new Color(1, 1, 1, 0);
        var t = CreateTween();
        t.TweenProperty(_mask, "modulate",
            wantMask ? Colors.White : new Color(1, 1, 1, 0),
            wantMask ? 0.25f : 0.3f);
        if (!wantMask)
            t.Finished += () =>
            {
                if (!CheckpointRollback.IsRollingBack)
                {
                    _mask.Visible = false;
                    Diag.Log("[CheckpointHud] 回退过渡遮罩已淡出");
                }
            };
    }
}
