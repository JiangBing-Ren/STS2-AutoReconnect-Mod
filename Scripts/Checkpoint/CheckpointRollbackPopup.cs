using System;
using System.Collections.Generic;
using Godot;
using AutoReconnect.Scripts.Ghost;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 主机检测到客机掉线时弹出的提示（取代旧版“瞬间自动回退主菜单”以及脆弱的 NErrorPopup 方案）。
///
/// 旧方案复用游戏内置 NErrorPopup + NModalContainer，其按钮子节点（_verticalPopup）依赖节点
/// _Ready 时序，在掉线回调语境下常取不到 → 弹窗不显示 / 按钮丢失（日志 “未找到 NVerticalPopup”）。
/// 本方案完全自绘：用 Godot Control 浮层（遮罩 + 面板 + 可滚动检查点列表 + 邀请/关闭按钮），
/// 主线程创建并挂在 tree.Root 上，不依赖任何游戏内部弹窗节点，100% 可控、必定显示。
///
/// 交互贴合需求流程：
///   - “邀请重连”按钮 → InviteHelper.TryInvite()（弹出 Steam 邀请）；弹窗保持打开，
///     等客机重连成功后主机可从列表选检查点回退。
///   - 检查点列表（最新在上）→ 点某个检查点 → CheckpointRollback.RollbackTo(checkpoint) 全队干净重载。
///   - “关闭”按钮 → 仅关弹窗，不做任何回退。
/// </summary>
internal static class CheckpointRollbackPopup
{
    private const string NodeName = "AutoReconnectMinDropPopup";

    /// <summary>主机检测到 playerId 掉线时调用，弹出邀请/回退提示（自动切主线程）。</summary>
    internal static void Show(ulong droppedPlayerId)
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null)
            {
                // 极端降级：没有场景树就不做主线程跳板，直接建（通常不会走到这里）。
                Build(droppedPlayerId);
                return;
            }
            void OnFrame()
            {
                tree.ProcessFrame -= OnFrame;
                Build(droppedPlayerId);
            }
            tree.ProcessFrame += OnFrame;
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 排程掉线弹窗失败：{ex}");
        }
    }

    /// <summary>主线程创建浮层。必须主线程：Godot 节点的 AddChild / _Ready 只能主线。</summary>
    private static void Build(ulong droppedPlayerId)
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            var root = tree?.Root;
            if (root == null)
            {
                Diag.Log("[Checkpoint] 无法获取场景树根，放弃显示掉线弹窗。");
                return;
            }

            // 防重复：若已存在先移除
            var existing = root.GetNodeOrNull<Control>(NodeName);
            existing?.QueueFree();

            var overlay = new Control
            {
                Name = NodeName,
                MouseFilter = Control.MouseFilterEnum.Stop, // 遮罩挡住游戏输入，形成模态
                ZIndex = 1000,
            };
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(overlay);

            // 半透明遮罩
            var dim = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.6f),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            overlay.AddChild(dim);

            // 居中面板
            var panel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(580, 540),
            };
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            overlay.AddChild(panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 14);
            panel.AddChild(vbox);

            var title = new Label
            {
                Text = $"队友掉线：{droppedPlayerId}",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            title.AddThemeFontSizeOverride("font_size", 22);
            vbox.AddChild(title);

            var subtitle = new Label
            {
                Text = "可先「邀请重连」把队友拉回，再选一个检查点全队干净重载；\n或直接从列表回退到某个检查点。",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            vbox.AddChild(subtitle);

            // 检查点列表（可滚动）
            var scroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
                CustomMinimumSize = new Vector2(0, 280),
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            vbox.AddChild(scroll);

            var listBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            listBox.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(listBox);

            var checkpoints = CheckpointStore.GetAll();
            if (checkpoints.Count == 0)
            {
                var none = new Label
                {
                    Text = "（暂无可用检查点，请先继续游戏触发节点保存）",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                listBox.AddChild(none);
            }
            else
            {
                // 最新在上
                for (int i = checkpoints.Count - 1; i >= 0; i--)
                {
                    var cp = checkpoints[i];
                    int num = i + 1;
                    bool latest = i == checkpoints.Count - 1;
                    var btn = new Button
                    {
                        Text = latest
                            ? $"回退到检查点 #{num}（最新）· 玩家 {cp.Players?.Count ?? 0} 人"
                            : $"回退到检查点 #{num} · 玩家 {cp.Players?.Count ?? 0} 人",
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    };
                    btn.AddThemeFontSizeOverride("font_size", 18);
                    int idx = i; // 闭环捕获，每次迭代独立
                    btn.Pressed += () =>
                    {
                        Diag.Log($"[Checkpoint] 主机选择回退到检查点 #{idx + 1}（玩家 {cp.Players?.Count ?? 0} 人）。");
                        overlay.QueueFree();
                        CheckpointRollback.RollbackTo(cp);
                    };
                    listBox.AddChild(btn);
                }
            }

            // 操作按钮行
            var hbox = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            hbox.AddThemeConstantOverride("separation", 16);
            vbox.AddChild(hbox);

            var inviteBtn = new Button
            {
                Text = "邀请重连",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            inviteBtn.AddThemeFontSizeOverride("font_size", 18);
            inviteBtn.Pressed += () =>
            {
                Diag.Log("[Checkpoint] 主机点击「邀请重连」，弹出 Steam 邀请对话框（弹窗保持打开，待客机重连后可选检查点回退）。");
                InviteHelper.TryInvite();
            };
            hbox.AddChild(inviteBtn);

            var closeBtn = new Button
            {
                Text = "关闭",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            closeBtn.AddThemeFontSizeOverride("font_size", 18);
            closeBtn.Pressed += () =>
            {
                Diag.Log("[Checkpoint] 主机关闭掉线弹窗（未做回退）。");
                overlay.QueueFree();
            };
            hbox.AddChild(closeBtn);

            Diag.Log($"[Checkpoint] 已显示掉线弹窗（检查点 {checkpoints.Count} 个）。主机可邀请重连或选检查点回退。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 创建掉线弹窗失败：{ex}");
        }
    }
}
