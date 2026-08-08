using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

// NVerticalPopup.InitNoButton 的第二个参数是 Action<NButton>，不是 Action。
// 用别名避免与 System.Action 混淆。
using NButton = MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton;

namespace AutoReconnect.Scripts.Ghost;

/// <summary>
/// 复用原版“连接失败，无法加入已开始游戏”弹窗（NErrorPopup），
/// 在重连被拦截时弹出原版同款样式的提示：“等本场战斗结束后再重连”，
/// 并额外追加一个“重试”按钮，让使用者直接重连，无需先关闭弹窗。
///
/// 实现要点（已通过 IL 反编译 sts2.dll 确认）：
///   - 原版弹窗由 NErrorPopup 承载，场景由 PreloadManager 缓存的 _scenePath 实例化；
///   - NErrorPopup.Create(string title, string body, bool showReportBugButton) 注入自定义文本；
///   - 弹窗需加入 NModalContainer 才会显示（原版 JoinGameAsync 即如此：NModalContainer.Instance.Add(popup)）。
///   - NErrorPopup 内部用 NVerticalPopup 承载按钮，其 InitNoButton(LocString, Action) 可把默认隐藏的
///     “取消”按钮显示出来并绑定回调（点击还会自动关闭弹窗）。我们用它追加“重试”按钮。
/// </summary>
internal static class ReconnectBlockedPopup
{
    /// <summary>是否在重连被拦截时显示自定义提示（可由 ModConfig 切换）。</summary>
    public static bool ShowOnBlocked { get; set; } = true;

    /// <summary>
    /// 以原版 NErrorPopup 样式显示“等本场战斗结束后再重连”提示，并附带“重试”按钮。
    /// 兼容未安装 ModConfig 的情况：仅作提示，不影响重连逻辑。
    /// </summary>
    internal static void Show(string? customBody = null)
    {
        try
        {
            var popup = NErrorPopup.Create(
                "连接失败",
                customBody ?? "等本场战斗结束后再重连。",
                false);
            var container = NModalContainer.Instance;
            if (popup != null && container != null)
            {
                container.Add(popup);
                ScheduleRetryButton(popup);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 显示重连拦截弹窗失败：{ex}");
        }
    }

    /// <summary>
    /// 弹窗加入场景树（_Ready 已执行、默认“取消”按钮已 HideNoButton）后，
    /// 给原版 NVerticalPopup 追加一个“重试”按钮。延迟一帧确保 _Ready 已完成。
    /// </summary>
    internal static void ScheduleRetryButton(NErrorPopup popup)
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null)
            {
                AddRetryButton(popup);
                return;
            }

            void OnFrame()
            {
                tree.ProcessFrame -= OnFrame;
                AddRetryButton(popup);
            }
            tree.ProcessFrame += OnFrame;
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 安排重试按钮失败：{ex}");
        }
    }

    private static void AddRetryButton(NErrorPopup popup)
    {
        try
        {
            var vp = GetVerticalPopup(popup);
            if (vp == null) return;

            // NVerticalPopup.InitNoButton(LocString label, Action<NButton> onClick)
            // 注意：第二个参数是 Action<NButton>，不是 System.Action（之前传 Action 导致
            // ArgumentException: Object of type 'System.Action' cannot be converted to
            // 'System.Action`1[...NButton]'）。这里精确解析该重载并传入 Action<NButton>。
            var locStringType = typeof(LocString);
            var actionNButtonType = typeof(Action<>).MakeGenericType(typeof(NButton));
            var initNo = vp.GetType().GetMethod("InitNoButton",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { locStringType, actionNButtonType }, null);
            if (initNo == null)
            {
                // 兜底：拿第一个同名方法（极少数版本签名不同）
                initNo = vp.GetType().GetMethod("InitNoButton",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (initNo == null)
            {
                Diag.Log("[AutoReconnect] 未找到 NVerticalPopup.InitNoButton，跳过重试按钮。");
                return;
            }

            // Bug B 修复（v0.6.1）：LocString 的构造签名其实是 (locTable, locEntryKey)，
            // 两个参数都是"表名/键名"，不是（键名+回退文本）。之前传 ("AUTORECONNECT_RETRY","重试")
            // 会被当作查找表 "AUTORECONNECT_RETRY" 的键 "重试"，该表不存在 → 渲染时抛
            // LocException("loc table='AUTORECONNECT_RETRY' does not exist!") → 弹窗/重试按钮崩溃。
            // 正确做法：把自定义键并入一个真实存在的 loc 表（LocTable.MergeWith 是 public），
            // 再用该表名构造 LocString，这样 GetRawText/GetFormattedText 都能查到"重试"。
            var retryLabel = CreateRetryLabel();
            if (retryLabel == null)
            {
                Diag.Log("[AutoReconnect] 无法创建重试按钮文案（loc 表尚未就绪），跳过重试按钮。");
                return;
            }
            // Action<NButton>：点击时忽略按钮参数，直接触发重连。
            var onRetry = new Action<NButton>(_ => TriggerRetry());
            initNo.Invoke(vp, new object[] { retryLabel, onRetry });
            Diag.Log("[AutoReconnect] 已为弹窗添加“重试”按钮。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 添加重试按钮失败：{ex}");
        }
    }

    private static object? GetVerticalPopup(NErrorPopup popup)
    {
        try
        {
            var f = popup.GetType().GetField("_verticalPopup",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(popup);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 创建"重试"按钮文案的 LocString（Bug B 修复）。
    /// 把自定义键 AUTORECONNECT_RETRY→"重试" 并入任意一个已存在的 loc 表，再用该表名构造 LocString，
    /// 确保 GetRawText()/GetFormattedText() 都能查到文案，不再抛 LocException。
    /// 若 loc 系统尚未加载（_tables 为空）则返回 null，调用方据此跳过按钮。
    /// </summary>
    private static LocString? CreateRetryLabel()
    {
        const string key = "AUTORECONNECT_RETRY";
        const string text = "重试";
        try
        {
            var lm = LocManager.Instance;
            if (lm == null) return null;

            var tablesField = typeof(LocManager).GetField("_tables",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (tablesField?.GetValue(lm) is not Dictionary<string, LocTable> tables || tables.Count == 0)
                return null;

            // 取任意一个已存在的表，把自定义键并进去（LocTable.MergeWith 是 public API）。
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
            Diag.Log($"[AutoReconnect] 注册重试按钮文案失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// “重试”按钮回调：直接启动客户端重连流程（ReconnectRunner）。
    /// 若已有重连流程正在努力重连则跳过，避免重复；
    /// 若旧流程已放弃（失败/成功，_isRunning=false）则先清理再启动全新流程，
    /// 否则连续点击“重试”会被旧节点挡住而失效。
    /// </summary>
    private static void TriggerRetry()
    {
        try
        {
            // 菜单重连语境（游戏被 Steam 邀请重拉起）：走 MenuRejoinFlow.RetryRejoin，
            // 重新握手 + 重建对局。不能走对局内 ReconnectRunner（它要求 RunManager 有内存中的
            // run，菜单场景为 null，会永远失败、陷入无限重试循环）。
            if (MenuRejoin.MenuRejoinFlow.PendingRejoinHostSteamId != 0)
            {
                // 故意 fire-and-forget：TriggerRetry 是弹窗按钮回调（void），
                // 重试在内部自有完备的异常处理与日志。
                var _ = MenuRejoin.MenuRejoinFlow.RetryRejoin();
                return;
            }

            var tree = Engine.GetMainLoop() as SceneTree;
            var existing = tree?.Root.GetNodeOrNull("AutoReconnectMin_Runner");
            if (existing != null)
            {
                // 通过 _isRunning 私有字段判断旧 runner 是否还在跑：
                //   true  → 自动重连仍在进行，交给它自己，不要叠加。
                //   false → 旧流程已放弃（OnReconnectFailed 已置 false 但节点未释放），清理后重启。
                bool stillRunning = false;
                try
                {
                    var f = existing.GetType().GetField("_isRunning",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    stillRunning = f != null && f.GetValue(existing) is true;
                }
                catch { }

                if (stillRunning)
                {
                    Diag.Log("[AutoReconnect] 重连流程仍在运行，跳过重试。");
                    return;
                }

                existing.QueueFree();
                Diag.Log("[AutoReconnect] 清理已放弃的旧 AutoReconnectMin_Runner，准备重新启动。");
            }

            // 延迟一帧：等旧节点 QueueFree 完成、名字释放后再添加新节点，
            // 避免与旧节点同名冲突，也让弹窗关闭动画先走完。
            if (tree == null)
            {
                SpawnRunner(null);
                return;
            }

            void OnFrame()
            {
                tree.ProcessFrame -= OnFrame;
                SpawnRunner(tree);
            }
            tree.ProcessFrame += OnFrame;
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 重试启动失败：{ex}");
        }
    }

    private static void SpawnRunner(SceneTree? tree)
    {
        try
        {
            var node = new ReconnectRunner();
            node.Name = "AutoReconnectMin_Runner";
            // 点击“重试”时从当前 NetService 捕获主机 Steam ID，供 FromPlayer 重建连接。
            try
            {
                if (RunManager.Instance is { NetService: NetClientGameService csv })
                    node.HostSteamId = csv.HostNetId;
            }
            catch { }
            tree?.Root.AddChild(node);
            Diag.Log("[AutoReconnect] 用户点击“重试”，启动重连流程。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 创建 ReconnectRunner 失败：{ex}");
        }
    }
}

/// <summary>
/// 拦截原版 NErrorPopup.Create(NetErrorInfo)：
/// 当错误原因为 RunInProgress（即“无法加入已开始游戏/重连被拒”）且本端为联机客户端时，
/// 用自定义文本替换原版弹窗内容，并追加“重试”按钮。
/// </summary>
[HarmonyPatch(typeof(NErrorPopup), "Create", new[] { typeof(NetErrorInfo) })]
public static class RejoinBlockedPopupPatch
{
    static bool Prepare() => true;

    public static void Postfix(NetErrorInfo info, ref NErrorPopup __result)
    {
        if (!ReconnectBlockedPopup.ShowOnBlocked) return;

        NetError reason;
        try { reason = info.GetReason(); }
        catch { return; }

        if (reason != NetError.RunInProgress) return;

        // 仅当本端是联机客户端（正在重连/加入已开始对局）时替换提示
        bool isClient = false;
        try
        {
            isClient = RunManager.Instance is { NetService: { } ns }
                       && ns.Type == NetGameType.Client;
        }
        catch { }

        // v0.7.0：客机若是「游戏重开后经 Steam 邀请重连」，此刻还停在主菜单，
        // RunManager.NetService 是 null，上面的 isClient 必然为 false。
        // MenuRejoin 流程会在识别出这是一次 rejoin 尝试时置起该标志，
        // 让这种场景也能拿到中文的「等本场战斗结束后再重连」+ 重试按钮。
        if (!isClient && !MenuRejoin.MenuRejoinFlow.LastAttemptWasRejoin) return;

        try
        {
            var custom = NErrorPopup.Create("连接失败", "等本场战斗结束后再重连。", false);
            if (custom != null)
            {
                __result = custom;
                // 调用方（JoinGameAsync）会把 __result 加入 NModalContainer，
                // 这里只给即将显示的弹窗追加重试按钮，避免再 Show 一次造成双弹窗。
                ReconnectBlockedPopup.ScheduleRetryButton(custom);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 自定义重连拦截弹窗创建失败：{ex}");
        }
    }
}
