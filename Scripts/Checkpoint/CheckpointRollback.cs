using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 全队回退到检查点（仅主机调用）。
/// 流程（与 QuickLink 恢复管线一致，已反编译确认）：
///   1. await NGame.Instance.ReturnToMainMenuAfterRun() —— 全员退回主菜单
///   2. RunManager.CanonicalizeSave(checkpoint, localPlayerId) —— 规范化检查点
///   3. new NetHostGameService() + StartSteamHost(玩家数) —— 以检查点重新托管
///   4. NMultiplayerLoadGameScreen.InitializeAsHost(netService, checkpoint) —— 原生多人读档界面
///   5. SubmenuStack.Push(screen) —— 全员加载同一份 SerializableRun
/// 因为全员加载的是同一份干净检查点，状态完全一致，确定性锁步 + ChecksumTracker 满意 → 不再分歧。
/// 掉线玩家重连到本主机（新托管会话）即可加入同一检查点。
///
/// 关键修复（v0.8.0-min）：
///   - 整段回退逻辑必须在【主线程】执行。旧版用 Task.Run 丢到线程池，
///     AddChildSafely 的延迟 AddChild 不在主线程兑现 → _Ready 永不触发 →
///     _remotePlayerContainer 为 null → InitializeAsHost 内 PlayerConnected 抛 NRE。
///   - 复用缓存的、已 _Ready 的子菜单节点（GetSubmenuType&lt;T&gt; 直取），避免新建节点。
///   - 加“就绪守卫”：若 _remotePlayerContainer 仍为空，await 主线程帧直到就绪，杜绝 NRE。
///   - 回退不再在掉线瞬间自动触发，而是由主机在弹窗里主动确认（见 CheckpointRollbackPopup）。
/// </summary>
internal static class CheckpointRollback
{
    /// <summary>
    /// Min 版本核心开关：主机检测到客机掉线时，全队回退到最近检查点。
    /// 默认开启（这就是 Min 版本存在的意义）；关闭则退回默认单人重连行为。
    /// </summary>
    public static bool Enabled = true;

    /// <summary>已就此掉线玩家弹过提示，避免重复弹窗（按 Steam ID 记录）。</summary>
    private static readonly HashSet<ulong> NotifiedDrops = new();

    public static bool TryMarkNotified(ulong id)
    {
        lock (NotifiedDrops)
        {
            if (NotifiedDrops.Contains(id)) return false;
            NotifiedDrops.Add(id);
            return true;
        }
    }

    public static void ClearNotified(ulong id)
    {
        lock (NotifiedDrops) NotifiedDrops.Remove(id);
    }

    public static void ClearAllNotified()
    {
        lock (NotifiedDrops) NotifiedDrops.Clear();
    }

    /// <summary>回退到指定检查点（内部会在主线程执行，可随时调用）。</summary>
    public static void RollbackTo(SerializableRun checkpoint)
    {
        _ = RunOnMainThread(async () => await DoRollback(checkpoint));
    }

    /// <summary>回退到最近一个检查点（无则提示，不崩溃）。</summary>
    public static void RollbackToLatest()
    {
        var cp = CheckpointStore.Latest;
        if (cp == null)
        {
            Diag.Log("[Checkpoint] 无可用检查点，放弃回退。");
            Notify("无法回退", "当前尚无可用检查点，无法回退。可先邀请队友重连，或继续游戏触发节点保存后再试。");
            return;
        }
        RollbackTo(cp);
    }

    private static Task RunOnMainThread(Func<Task> action)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            // 极端降级：没有场景树就不做主线程跳板，直接跑（通常不会走到这里）。
            return RunAsync(action);
        }
        // 跳到主线程，确保后续 Godot 节点操作（AddChildSafely / _Ready）在主线程兑现。
        void OnFrame()
        {
            tree.ProcessFrame -= OnFrame;
            _ = RunAsync(action);
        }
        tree.ProcessFrame += OnFrame;
        return Task.CompletedTask;
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 回退执行异常：{ex}");
        }
    }

    private static async Task DoRollback(SerializableRun checkpoint)
    {
        if (checkpoint == null) return;

        // 回退开始即清空“已通知”集合：新的一局会重新触发提示逻辑。
        ClearAllNotified();

        var rm = RunManager.Instance;
        // 放宽：单人/离线（NetService == null）或主机（Type == Host）均可回退；
        // 客机（Type == Client）不能重新托管整局，拒绝。
        bool canRollback = rm != null && (rm.NetService == null || rm.NetService.Type == NetGameType.Host);
        if (!canRollback)
        {
            Diag.Log("[Checkpoint] 非主机或不在对局中，放弃回退。");
            return;
        }

        Diag.Log("[Checkpoint] 开始全队回退到检查点（所有人将返回大厅并加载同一检查点）...");
        var game = NGame.Instance;
        if (game == null)
        {
            Diag.Log("[Checkpoint] NGame.Instance 为空，放弃回退。");
            return;
        }

        // 1) 全员退回主菜单（场景切换在主线程，await 后切回主线程再做节点操作）
        await game.ReturnToMainMenuAfterRun();
        await AwaitMainThread();

        var mainMenu = game.MainMenu;
        if (mainMenu == null)
        {
            Diag.Log("[Checkpoint] 回退后主菜单为空，放弃。");
            return;
        }

        // 给主菜单/子菜单栈一两拍完成 EnterTree（若 ReturnToMainMenu 重建了菜单）。
        await AwaitMainThread();
        await AwaitMainThread();

        // 2) 规范化检查点（与 QuickLink 一致：修正本地玩家槽位/序列化版本等）
        ulong localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        SerializableRun canonical;
        try
        {
            canonical = RunManager.CanonicalizeSave(checkpoint, localPlayerId);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 规范化检查点失败，回退使用原始检查点：{ex.Message}");
            canonical = checkpoint;
        }
        int maxClients = canonical.Players?.Count ?? 4;

        // 3) 以检查点重新托管
        var netService = new NetHostGameService();
        NetErrorInfo? err = await netService.StartSteamHost(maxClients);
        if (err.HasValue)
        {
            Diag.Log($"[Checkpoint] 重新托管失败：{err}");
            return;
        }

        // 4) 原生多人读档界面（缓存节点，通常已 _Ready）
        var screen = mainMenu.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
        if (screen == null)
        {
            Diag.Log("[Checkpoint] 取 NMultiplayerLoadGameScreen 失败，放弃。");
            return;
        }

        // 就绪守卫：确保 _remotePlayerContainer 已初始化（杜绝 NRE）
        await EnsureScreenReady(screen);

        // 5) 以主机身份用同一份检查点初始化并推入子菜单栈
        screen.InitializeAsHost(netService, canonical);
        mainMenu.SubmenuStack.Push(screen);
        Diag.Log("[Checkpoint] 已推送检查点读档界面。全队将加载同一检查点；掉线玩家重连到本主机即可加入。");
    }

    /// <summary>跳回主线程一帧（Godot 信号驱动的 await，保证在主线兑现）。</summary>
    private static async Task AwaitMainThread()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>
    /// 等待 NMultiplayerLoadGameScreen 的 _remotePlayerContainer 就绪。
    /// 若该节点是本次新建（AddChildSafely 延迟了 _Ready），则多等几帧。
    /// </summary>
    private static async Task EnsureScreenReady(NMultiplayerLoadGameScreen screen)
    {
        try
        {
            var f = typeof(NMultiplayerLoadGameScreen).GetField("_remotePlayerContainer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < 30; i++)
            {
                if (f?.GetValue(screen) != null) return;
                await AwaitMainThread();
            }
            Diag.Log("[Checkpoint] 警告：_remotePlayerContainer 在等待后仍未就绪，仍尝试 InitializeAsHost（可能仍会 NRE，请反馈日志）。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 就绪检查异常：{ex.Message}");
        }
    }

    /// <summary>通用信息弹窗（复用原版 NErrorPopup 样式）。供 HUD/弹窗统一调用。</summary>
    public static void Notify(string title, string body)
    {
        try
        {
            var popup = NErrorPopup.Create(title, body, false);
            var container = NModalContainer.Instance;
            if (popup != null && container != null)
                container.Add(popup);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 提示弹窗创建失败：{ex}");
        }
    }
}
