using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using AutoReconnect.Scripts.Ghost;

using NButton = MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 主机检测到客机掉线时弹出的提示（取代旧版的“瞬间自动回退主菜单”）。
/// 复用原版 NErrorPopup + NModalContainer 样式，并挂载两个按钮：
///   “回退到检查点”（Yes）→ CheckpointRollback.RollbackToLatest()
///   “邀请重连”   （No） → InviteHelper.TryInvite()
/// 两个按钮点击后都会自动关闭弹窗（NVerticalPopup 的 InitXxxButton 内部已绑定 Close）。
///
/// 文案通过 LocTable.MergeWith 注入一个临时的 key（复用任意已存在 loc 表），
/// 避免直接拼 (key, fallbackText) 触发 LocException（详见 ReconnectBlockedPopup 的同类处理）。
/// </summary>
internal static class CheckpointRollbackPopup
{
    /// <summary>主机检测到玩家 dropPlayerId 掉线时调用，弹出邀请/回退提示。</summary>
    internal static void Show(ulong droppedPlayerId)
    {
        try
        {
            var popup = NErrorPopup.Create(
                "队友掉线",
                $"玩家 {droppedPlayerId} 已掉线。\n可邀请其重连，或全队回退到最近检查点。",
                false);
            var container = NModalContainer.Instance;
            if (popup == null || container == null)
            {
                Diag.Log("[Checkpoint] 无法创建掉线弹窗（popup/container 为空）。");
                return;
            }
            container.Add(popup);
            ScheduleButtons(popup);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 显示掉线弹窗失败：{ex}");
        }
    }

    private static void ScheduleButtons(NErrorPopup popup)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            AddButtons(popup);
            return;
        }
        // 延迟一帧，确保弹窗已进入场景树、_Ready 完成、按钮可安全配置。
        void OnFrame()
        {
            tree.ProcessFrame -= OnFrame;
            AddButtons(popup);
        }
        tree.ProcessFrame += OnFrame;
    }

    private static void AddButtons(NErrorPopup popup)
    {
        try
        {
            var vp = GetVerticalPopup(popup);
            if (vp == null)
            {
                Diag.Log("[Checkpoint] 未找到 NVerticalPopup，跳过重连/回退按钮。");
                return;
            }

            // “回退到检查点” = Yes 按钮
            var yesLabel = CreateLabel("AUTORECONNECT_ROLLBACK", "回退到检查点");
            if (yesLabel != null)
            {
                var onYes = new Action<NButton>(_ => CheckpointRollback.RollbackToLatest());
                InvokeInit(vp, "InitYesButton", yesLabel, onYes);
                // 某些 NErrorPopup 默认隐藏 Yes，强制显示。
                ForceVisible(vp, "YesButton");
            }

            // “邀请重连” = No 按钮
            var noLabel = CreateLabel("AUTORECONNECT_INVITE", "邀请重连");
            if (noLabel != null)
            {
                var onNo = new Action<NButton>(_ => InviteHelper.TryInvite());
                InvokeInit(vp, "InitNoButton", noLabel, onNo);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 添加掉线弹窗按钮失败：{ex}");
        }
    }

    private static void InvokeInit(object vp, string methodName, LocString label, Action<NButton> onPressed)
    {
        var locStringType = typeof(LocString);
        var actionNButtonType = typeof(Action<>).MakeGenericType(typeof(NButton));
        var m = vp.GetType().GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { locStringType, actionNButtonType }, null);
        if (m == null)
        {
            Diag.Log($"[Checkpoint] 未找到 {methodName}，跳过重连/回退按钮。");
            return;
        }
        m.Invoke(vp, new object[] { label, onPressed });
    }

    private static void ForceVisible(object vp, string propertyName)
    {
        try
        {
            var prop = vp.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop?.GetValue(vp) is Godot.Control btn)
            {
                btn.Visible = true;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 强制显示 {propertyName} 失败：{ex.Message}");
        }
    }

    private static object? GetVerticalPopup(NErrorPopup popup)
    {
        var f = popup.GetType().GetField("_verticalPopup",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(popup);
    }

    /// <summary>
    /// 把自定义键并入任意一个已存在的 loc 表，再用该表名构造 LocString，
    /// 确保 GetFormattedText() 能查到文案，不再抛 LocException。
    /// 若 loc 系统尚未加载（_tables 为空）则返回 null，调用方据此跳过按钮。
    /// </summary>
    private static LocString? CreateLabel(string key, string text)
    {
        try
        {
            var lm = LocManager.Instance;
            if (lm == null) return null;

            var tablesField = typeof(LocManager).GetField("_tables",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (tablesField?.GetValue(lm) is not Dictionary<string, LocTable> tables || tables.Count == 0)
                return null;

            foreach (var kv in tables)
            {
                if (kv.Value == null) continue;
                kv.Value.MergeWith(new Dictionary<string, string> { { key, text } });
                return new LocString(kv.Key, key);
            }
            return null;
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 注册按钮文案失败：{ex.Message}");
            return null;
        }
    }
}
