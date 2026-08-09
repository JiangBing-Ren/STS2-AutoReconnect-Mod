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
/// 全队回退到检查点（仅主机/单人调用）。
/// 流程（与 QuickLink 恢复管线一致，已反编译确认）：
///   1. RunManager.CanonicalizeSave(checkpoint, localPlayerId) —— 规范化检查点（失败则回退用原始检查点）。
///   2a. 单人/离线：原地无缝回退（参考 Rewind 皮皮倒带 v0.26.18 验证过的序列）：
///         await NGame.Transition.FadeOut(黑屏)
///         → RunManager.CleanUp(true)                                  （State=null，满足 SetUpSaved* 前置）
///         → RunManager.SetUpSavedSingleplayer(rs, canonical)
///         → NGame.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService())  ★ 关键
///         → NGame.LoadRun(rs, preFinishedRoom)
///         → NGame.Transition.FadeIn
///       不回主菜单、无闪屏。注意：v0.8.6 失败的根因正是缺 InitializeNetworking（而非“必须主菜单场景”）。
///   2b. 多人主机：await ReturnToMainMenuAfterRun() 退回主菜单（重托管需要主菜单/子菜单栈）→ new NetHostGameService()
///       → NMultiplayerLoadGameScreen.InitializeAsHost(netService, canonical)
///       → SubmenuStack.Push(screen)
///       → screen.BeginRun()  ← 关键：推屏后必须真正“开始对局”，否则全员卡在读档大厅
///          （BeginRun 内部：SetUpSavedMultiplayer + NGame.LoadRun，全员加载同一份 SerializableRun）
/// 因为全员加载的是同一份干净检查点，状态完全一致，确定性锁步 + ChecksumTracker 满意 → 不再分歧。
/// 注：HUD 的回退遮罩会盖住“回主菜单”过程，用户无感（见 CheckpointHud）。
///
/// 关于“一起回到检查点”：
///   - 单人：SetUpSavedSingleplayer 直接载入，本人即进入检查点。
///   - 多人主机：BeginRun 后【主机】立即进入检查点。其余已连接的客户端需重连本主机的新托管会话
///     才能加入（客户端主动发 ClientLobbyJoinRequestMessage / ClientRejoinRequestMessage 进入新 lobby）。
///     当前版本主机侧已正确开打；客户端自动重连 glue 为独立待办（见代码末注释）。
///
/// 关键修复（v0.8.x）：
///   - 整段回退逻辑必须在【主线程】执行（旧版 Task.Run 导致 _remotePlayerContainer 为 null → NRE）。
///   - 复用缓存的、已 _Ready 的子菜单节点（GetSubmenuType&lt;T&gt; 直取）。
///   - 加“就绪守卫”：若 _remotePlayerContainer 仍为空，await 主线程帧直到就绪，杜绝 NRE。
///   - v0.8.5 修复：推屏后调用 BeginRun()，否则只推屏不开始 → 全员卡在读档大厅而非进入检查点。
///   - v0.8.5 修复：允许 Singleplayer 回退（旧版把 Singleplayer 误判为不可回退）。
/// </summary>
internal static class CheckpointRollback
{
    /// <summary>
    /// Min 版本核心开关：主机检测到客机掉线时，全队回退到最近检查点。
    /// 默认开启（这就是 Min 版本存在的意义）；关闭则退回默认单人重连行为。
    /// </summary>
    public static bool Enabled = true;

    /// <summary>是否正在回退中（HUD 与掉线弹窗两条触发路径都经 RollbackTo，统一由此标志驱动遮罩）。</summary>
    public static bool IsRollingBack { get; private set; }

    /// <summary>当前回退是否为单人/离线回退（决定 HUD 是否叠加手画遮罩：单人用原生 Transition 遮罩，不叠）。</summary>
    public static bool IsRollingBackSingleplayer { get; private set; }

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

    /// <summary>回退到指定检查点（在遮罩下于主线程执行；返回 Task 便于 HUD 等待回退完成后再淡出遮罩）。</summary>
    public static Task RollbackTo(SerializableRun checkpoint)
    {
        if (checkpoint == null) return Task.CompletedTask;
        ClearAllNotified();
        // 同步置位回退状态（在任何帧切换之前），HUD 据此统一驱动遮罩，避免弹窗路径漏遮罩。
        var rm = RunManager.Instance;
        IsRollingBackSingleplayer = rm == null
            || rm.NetService == null
            || rm.NetService.Type == NetGameType.Singleplayer;
        IsRollingBack = true;
        return RunOnMainThread(async () =>
        {
            try { await DoRollback(checkpoint); }
            finally { IsRollingBack = false; }
        });
    }

    private static Task RunOnMainThread(Func<Task> action)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            // 极端降级：没有场景树就不做主线程跳板，直接跑（通常不会走到这里）。
            return RunAsync(action);
        }
        // 跳到主线程，确保后续 Godot 节点操作（AddChildSafely / _Ready）在主线程兑现；
        // 用 TaskCompletionSource 把“主线程执行完 action”的结果透传出去，便于调用方 await。
        var tcs = new TaskCompletionSource<bool>();
        async void OnFrame()
        {
            tree.ProcessFrame -= OnFrame;
            try
            {
                await RunAsync(action);
            }
            catch (Exception ex)
            {
                Diag.Log($"[Checkpoint] 回退执行异常（外层）：{ex}");
            }
            finally
            {
                tcs.TrySetResult(true);
            }
        }
        tree.ProcessFrame += OnFrame;
        return tcs.Task;
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

        var rm = RunManager.Instance;
        // 允许：主菜单（NetService == null，理论上 HUD 仅对局中显示）、单人（Singleplayer）、主机（Host）。
        // 客机（Client）不能重新托管整局，拒绝。
        bool canRollback = rm != null && (rm.NetService == null
            || rm.NetService.Type == NetGameType.Singleplayer
            || rm.NetService.Type == NetGameType.Host);
        if (!canRollback)
        {
            Diag.Log("[Checkpoint] 非主机/单人且不在对局中，放弃回退。");
            return;
        }

        // 记录回退前的会话类型（回退流程会清理 NetService，故先记下）。
        NetGameType prevType = rm!.NetService?.Type ?? NetGameType.Singleplayer;
        bool isMultiplayerHost = prevType == NetGameType.Host;

        Diag.Log($"[Checkpoint] 开始回退到检查点（会话类型={prevType}）...");
        var game = NGame.Instance;
        if (game == null)
        {
            Diag.Log("[Checkpoint] NGame.Instance 为空，放弃回退。");
            return;
        }

        // 规范化检查点（与 QuickLink 一致：修正本地玩家槽位/序列化版本等）。
        ulong localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        SerializableRun canonical = checkpoint;
        try
        {
            canonical = RunManager.CanonicalizeSave(checkpoint, localPlayerId);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 规范化检查点失败，回退使用原始检查点：{ex.Message}");
            canonical = checkpoint;
        }
        canonical ??= checkpoint; // 兜底：CanonicalizeSave 返回 null 时不崩溃

        // 单人 / 离线：原地无缝回退（参考 Rewind 皮皮倒带 v0.26.18 验证过的序列）。
        // 关键点：加载新 run 前必须显式重置 ReactionContainer 的网络栈
        // （InitializeNetworking(new NetSingleplayerGameService())），否则 LoadRun 内
        // RootSceneContainer.SetCurrentScene(NRun.Create) 会空转、场景不切换（v0.8.6 的坑）。
        if (!isMultiplayerHost)
        {
            try
            {
                await game.Transition.FadeOut(0.8f, "res://materials/transitions/fade_transition_mat.tres", null);
                rm!.CleanUp(true);
                await AwaitMainThread();
                var rs = RunState.FromSerializable(canonical);
                await rm.SetUpSavedSingleplayer(rs, canonical);
                game.ReactionContainer.InitializeNetworking((INetGameService)new NetSingleplayerGameService());
                await game.LoadRun(rs, canonical.PreFinishedRoom);
                await game.Transition.FadeIn(0.8f, "res://materials/transitions/fade_transition_mat.tres", null);
                Diag.Log("[Checkpoint] 单人回退完成，已进入检查点（无缝，未回主菜单）。");
            }
            catch (Exception ex)
            {
                Diag.Log($"[Checkpoint] 单人回退失败：{ex}");
            }
            return;
        }

        // 多人主机：清理当前 run 并退回主菜单（重托管流程需要主菜单/子菜单栈），
        // 然后以检查点重新托管并开始对局。HUD 遮罩盖住“回主菜单”过程，用户无感。
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

        int maxClients = canonical.Players?.Count ?? 4;

        // 以检查点重新托管（与 QuickLink 一致），并开始对局。
        var netService = new NetHostGameService();
        NetErrorInfo? err = await netService.StartSteamHost(maxClients);
        if (err.HasValue)
        {
            Diag.Log($"[Checkpoint] 重新托管失败：{err}");
            return;
        }

        // 原生多人读档界面（缓存节点，通常已 _Ready）
        var screen = mainMenu.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
        if (screen == null)
        {
            Diag.Log("[Checkpoint] 取 NMultiplayerLoadGameScreen 失败，放弃。");
            return;
        }

        // 就绪守卫：确保 _remotePlayerContainer 已初始化（杜绝 NRE）
        await EnsureScreenReady(screen);

        // 以主机身份用同一份检查点初始化并推入子菜单栈
        screen.InitializeAsHost(netService, canonical);
        mainMenu.SubmenuStack.Push(screen);

        // 关键：真正开始对局！否则全员只会停在读档大厅（之前的 bug）。
        //    BeginRun 内部 → StartRun → SetUpSavedMultiplayer + NGame.LoadRun，全员加载同一检查点。
        //    （注意：已连接的客户端需主动重连本主机的新托管会话才会被加入 lobby；本端仅负责开打。）
        Diag.Log("[Checkpoint] 已推送检查点读档界面，开始加载检查点...");
        bool beganOk = true;
        try
        {
            screen.BeginRun();
        }
        catch (Exception ex)
        {
            beganOk = false;
            Diag.Log($"[Checkpoint] 开始对局（BeginRun）异常：{ex}");
        }
        if (beganOk)
            Diag.Log("[Checkpoint] 主机回退完成：已以检查点重新托管并开始对局（全员将加载同一份检查点）。");
        await AwaitMainThread(); // 多等一帧，让遮罩覆盖 BeginRun 后的场景切换
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
