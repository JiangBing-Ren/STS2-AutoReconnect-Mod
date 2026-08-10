using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoReconnect.Scripts.MenuRejoin;
using AutoReconnect.Scripts.Net;
using AutoReconnect.Scripts.Reload;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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
///   2b. 多人主机（v0.9.0 根治，改为「原地保活重载」，参考 QuickSL 的 QuickSlMultiplayerReloader）：
///         await NGame.Transition.FadeOut(黑屏)
///         → 踢掉仍在线的客机（DisconnectClient，让它们各自走自动重连回同一大厅）
///         → NetServiceSwapper.CleanUpKeepingConnection(rm)   ★ 关键：抑制 CleanUp 里的 Disconnect
///         → new LoadRunLobby(原 NetService, PassiveRejoinLobbyListener, canonical) + AddLocalHostPlayer()
///         → ReactionContainer.InitializeNetworking(原 NetService)
///         → RunManager.SetUpSavedMultiplayer(rs, lobby) → lobby.CleanUp(disconnectSession:false)
///         → NGame.LoadRun(rs, canonical.PreFinishedRoom)
///         → NGame.Transition.FadeIn
///       全程 **不回主菜单、不 StartSteamHost、Steam 大厅 ID 不变**。
///
/// ## 为什么 v0.9.0 必须改掉「回主菜单 + 重新托管」
///
/// 旧路径 `ReturnToMainMenuAfterRun()` → `new NetHostGameService().StartSteamHost()` 会**换一个新的
/// Steam 大厅**。而掉线客机手里只有旧大厅 ID（HostInfoTracker 捕获的那个），于是：
///   - FromLobby 连旧大厅 → 大厅已销毁 → 连不上；
///   - FromPlayer 兜底 → GetFriendGamePlayed 时序不稳 → InvalidJoin。
/// 最终客机弹「发现未知错误」。这就是场景 B 的根因——不是握手 bug，是**大厅被换掉了**。
///
/// 根治办法就是让大厅活下来。唯一挡路的是 BaseGame `RunManager.CleanUp`（RunManager.cs:1606）：
/// <code>NetService.Disconnect(NetError.Quit, !graceful);</code>
/// 用 <see cref="AutoReconnect.Scripts.Net.NetServiceSwapper.CleanUpKeepingConnection"/> 临时把
/// NetService 换成断连抑制包装即可吞掉这一句，其余行为完全不变。
///
/// 关于“一起回到检查点”：
///   - 单人：SetUpSavedSingleplayer 直接载入，本人即进入检查点。
///   - 多人主机：主机原地进入检查点；**掉线客机**由自身 ReconnectRunner 自动重连回同一大厅，
///     基游戏 `RunLobby.HandleClientRejoinRequestMessage` 会把主机当前（=检查点）状态整份回传，
///     客机检测到 NumReloads 变化后走整局重建（见 ReconnectRunner / MenuRejoinFlow.RebuildRunInPlace）。
///     **仍在线的客机**在回退前被主动踢下线，走同一条自动重连路径回来。全程无需任何自定义网络消息。
///
/// 关键修复（v0.8.x）：
///   - 整段回退逻辑必须在【主线程】执行（旧版 Task.Run 导致 _remotePlayerContainer 为 null → NRE）。
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

    /// <summary>当前回退是否走原生 Transition 遮罩（决定 HUD 是否叠加手画遮罩）。
    /// 单人/离线回退与多人主机原地保活重载都用原生 NGame.Transition 淡入淡出，HUD 不再叠加黑遮罩，避免双重黑屏。
    /// 反之（例如旧版“回主菜单+重托管”路径）才需要 HUD 自己画遮罩。</summary>
    public static bool IsRollingBackNativeTransition { get; private set; }

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
        // 单人/离线与多人主机原地保活重载都走原生 NGame.Transition 遮罩，故 HUD 不叠手画遮罩。
        var rm = RunManager.Instance;
        IsRollingBackNativeTransition = rm == null
            || rm.NetService == null
            || rm.NetService.Type == NetGameType.Singleplayer
            || rm.NetService.Type == NetGameType.Host;
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
        // localPlayerId 必须取「当前对局 NetService 的本地玩家 NetId」而非 PlatformUtil.GetLocalPlayerId：
        //   - 单人局 NetService = NetSingleplayerGameService，其 NetId = 1（与存档 Players 中的槽位 id 一致）；
        //   - 多人主机 NetService = Steam 服务，其 NetId = 本地 Steam ID（与存档 Players 中的 steam id 一致）。
        // 二者均能在 CanonicalizeSave 中命中 save.Players，从而规范化成功。
        // 若仍用 PlatformUtil.GetLocalPlayerId（= 真实 Steam ID），单人局存档 Players 仅含 id=1，必报
        //   "Players does not contain local player Id" 而降级回退原始检查点（每局单人回退都打此告警）。
        ulong localPlayerId = rm!.NetService?.NetId ?? PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
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

        // ───────────────────────────────────────────────────────────────────────────
        // 多人主机（v0.9.0）：原地保活重载。不回主菜单、不重新托管、Steam 大厅 ID 不变。
        // ───────────────────────────────────────────────────────────────────────────
        await DoHostRollbackInPlace(game, rm, canonical);
    }

    /// <summary>
    /// 多人主机的「原地保活重载」。整段必须在主线程执行（由 <see cref="RollbackTo"/> 保证）。
    /// </summary>
    private static async Task DoHostRollbackInPlace(NGame game, RunManager rm, SerializableRun canonical)
    {
        var original = rm.NetService;
        if (original == null)
        {
            Diag.Log("[Checkpoint] 主机回退失败：NetService 为空。");
            return;
        }

        bool fadedOut = false;
        LoadRunLobby? lobby = null;

        try
        {
            // ── 1) 记录仍在线的客机。回退会让它们的内存状态整体失效，必须请它们重连回来。
            var stillConnected = new List<ulong>();
            var host = original as INetHostGameService;
            if (host != null)
            {
                try
                {
                    foreach (var peer in host.ConnectedPeers)
                    {
                        if (peer.peerId != original.NetId) stillConnected.Add(peer.peerId);
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log($"[Checkpoint] 枚举在线客机失败（按 0 人继续）：{ex.Message}");
                }
            }
            Diag.Log($"[Checkpoint] 主机原地回退开始：在线客机 {stillConnected.Count} 人，" +
                     $"存档玩家 {canonical.Players?.Count ?? 0} 人，NumReloads={canonical.NumReloads}。");

            await game.Transition.FadeOut(0.8f, "res://materials/transitions/fade_transition_mat.tres", null);
            fadedOut = true;

            // ── 2) 踢掉仍在线的客机。
            //    用 StateDivergence 而不是 Quit/Kicked：语义正是「你的状态已经和主机对不上，请整局重载」，
            //    客机侧 LocalPlayerDisconnectedPatch 对任何非自愿断线都会拉起 ReconnectRunner，
            //    重连后再通过 NumReloads 变化判定要走整局重建而不是简单换连接。
            //    大厅本身不动，所以它们连回来的还是同一个 Steam 大厅。
            foreach (var peerId in stillConnected)
            {
                try
                {
                    host!.DisconnectClient(peerId, NetError.StateDivergence);
                    Diag.Log($"[Checkpoint] 已请客机 {peerId} 重连（回退需要整局重载）。");
                }
                catch (Exception ex)
                {
                    Diag.Log($"[Checkpoint] 断开客机 {peerId} 失败（忽略）：{ex.Message}");
                }
            }

            // ── 2.5) 重载前显式释放旧局的 4 个网络同步器（借鉴 QuickSL DisposeNetworkPreservedRunSystems）。
            //    必须在 CleanUp 之前做：避免旧会话的同步器（战斗/事件/一次性/输入）残留引用，
            //    干扰下面 SetUpSavedMultiplayer 建立的新同步器，否则真·联机重载可能 StateDiverged / 握手冲突。
            //    注：AR 不依赖 JML，这里是手写等效（直接 Dispose RunManager 上的 4 个 IDisposable 同步器）。
            DisposeNetworkPreservedRunSystems(rm);

            // ── 3) 清理当前 run，但**保住连接**（核心）。
            NetServiceSwapper.CleanUpKeepingConnection(rm, graceful: true);
            await AwaitMainThread();

            // ── 4) 用同一条连接重开一个读档大厅。只放本机；缺席玩家留给基游戏 rejoin 机制补回。
            //    （RunLobby.Players 只记录“当前在线”的人，RunState.Players 才是全员，
            //      所以少人开局是基游戏原生支持的形态——正是 ghost / 重连能工作的前提。）
            lobby = new LoadRunLobby(original, PassiveRejoinLobbyListener.CreateListener(), canonical);
            lobby.AddLocalHostPlayer();

            // ── 5) 重置反应容器的网络栈（与单人分支同理；缺了它 LoadRun 的场景切换会空转）。
            game.ReactionContainer.InitializeNetworking(original);

            RejoinSceneReloadGuard.PrepareCurrentHandForSceneSwap();
            using (RejoinTransitionGuard.SuppressTransitions())
            using (RejoinSceneReloadGuard.SuppressLateHandLayoutRefresh())
            using (RejoinSceneReloadGuard.PreserveStableTopBarLocation())
            {
                var rs = RunState.FromSerializable(canonical);

                // ── 阶段1 屏障（两阶段 LoadBarrier 等效，主机侧）──
                //    QuickSL 此处等待所有在线客机上报「载入就绪」再统一开始载入；
                //    AR 的在线客机已被 StateDivergence 请离、随后独立重连（重载期间不在场），
                //    故此处退化为「主机侧就绪校验 + 让出一帧」，确保大厅/网络栈/输入同步器已就位。
                await HostLoadBarrierPhase1Async(rm, lobby);

                await rm.SetUpSavedMultiplayer(rs, lobby);

                // ── 6) 立刻注销读档大厅的消息处理器，**在 LoadRun 之前**。
                //    LoadRun 可能耗时数秒，期间 RunLobby 已经建好并注册了同名处理器
                //    （ClientRejoinRequestMessage / ClientLoadJoinRequestMessage / PlayerLeftMessage …）。
                //    若把 CleanUp 拖到 LoadRun 之后，正好在这窗口重连回来的客机会被两套 lobby 各处理一次，
                //    行为未定义。disconnectSession:false —— 只摘处理器，连接留给已接管的 RunManager。
                lobby.CleanUp(disconnectSession: false);
                lobby = null;

                // ── 阶段2 屏障（两阶段 LoadBarrier 等效，主机侧）──
                //    QuickSL 此处等待所有客机确认「同步器已注册」再统一开始 run；
                //    AR 无在场客机，故退化为「新 run 同步器就绪校验 + 让出一帧」，
                //    在 LoadRun 真正开始 tick 前确认 SetUpSavedMultiplayer 成功建立了战斗/事件/一次性/输入同步器。
                await HostLoadBarrierPhase2Async(rm);

                await game.LoadRun(rs, canonical.PreFinishedRoom);
            }

            await game.Transition.FadeIn(0.8f, "res://materials/transitions/fade_transition_mat.tres", null);
            fadedOut = false;

            Diag.Log("[Checkpoint] 主机回退完成：已原地载入检查点，Steam 大厅保持不变，等待客机自动重连。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 主机原地回退失败：{ex}");
            try { lobby?.CleanUp(disconnectSession: false); } catch { }

            // 兜底：状态可能半残（State 已被 CleanUp 清空但 LoadRun 没走完）。
            // 回主菜单是唯一安全的落点，至少不会卡在黑屏里。
            try
            {
                if (rm.DebugOnlyGetState() == null)
                {
                    Diag.Log("[Checkpoint] 回退中断且 run 已清空，退回主菜单以免卡死。");
                    await game.ReturnToMainMenu();
                }
                else if (fadedOut)
                {
                    await game.Transition.FadeIn(0.8f, "res://materials/transitions/fade_transition_mat.tres", null);
                }
            }
            catch (Exception recoverEx)
            {
                Diag.Log($"[Checkpoint] 回退失败后的兜底恢复又出错：{recoverEx.Message}");
            }
        }
    }

    #region 借鉴 QuickSL 的 ② DisposeNetworkPreservedRunSystems + ③ 两阶段 LoadBarrier（手写等效）

    /// <summary>
    /// ② 重载前显式释放旧局的 4 个网络同步器（QuickSL <c>DisposeNetworkPreservedRunSystems</c> 的手写等效）。
    /// 必须在 <c>CleanUpKeepingConnection</c> 之前调用：旧同步器若残留，会干扰新 run 的
    /// <c>SetUpSavedMultiplayer</c>，导致真·联机重载出现 <c>StateDiverged</c> / 握手冲突。
    /// AR 不引入 JML，故直接 Dispose <c>RunManager</c> 上的 4 个 <see cref="IDisposable"/> 同步器。
    /// </summary>
    private static void DisposeNetworkPreservedRunSystems(RunManager rm)
    {
        TryDisposeRunSystem("CombatStateSynchronizer", rm.CombatStateSynchronizer);
        TryDisposeRunSystem("EventSynchronizer", rm.EventSynchronizer);
        TryDisposeRunSystem("OneOffSynchronizer", rm.OneOffSynchronizer);
        TryDisposeRunSystem("InputSynchronizer", rm.InputSynchronizer);
    }

    private static void TryDisposeRunSystem(string name, IDisposable? disposable)
    {
        if (disposable == null)
        {
            Diag.Log($"[Checkpoint] 旧局同步器 {name} 为空，跳过释放。");
            return;
        }
        try
        {
            disposable.Dispose();
            Diag.Log($"[Checkpoint] 已释放旧局网络同步器 {name}。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 释放旧局网络同步器 {name} 异常（忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// ③ 阶段1 屏障（两阶段 LoadBarrier 的主机侧等效）。
    /// QuickSL 此处跨端等待「所有在线客机载入就绪」；AR 的在线客机已被 StateDivergence 请离、
    /// 随后独立重连（重载期间不在场），故退化为「主机侧就绪校验 + 让出一帧」。
    /// 校验大厅输入同步器与保活连接已就位，再放行 <c>SetUpSavedMultiplayer</c>。
    /// </summary>
    private static async Task HostLoadBarrierPhase1Async(RunManager rm, LoadRunLobby? lobby)
    {
        try
        {
            if (lobby == null)
                Diag.Log("[Checkpoint] 阶段1屏障：lobby 为空，跳过（不影响单机重载）。");
            else if (lobby.InputSynchronizer == null)
                Diag.Log("[Checkpoint] 阶段1屏障：警告——lobby.InputSynchronizer 为空，载入大厅可能不完整。");
            else
                Diag.Log("[Checkpoint] 阶段1屏障：载入大厅输入同步器已就位。");

            if (rm.NetService == null || !rm.NetService.IsConnected)
                Diag.Log("[Checkpoint] 阶段1屏障：警告——保活连接为空或未连接，重载后客机可能无法重连。");
            else
                Diag.Log("[Checkpoint] 阶段1屏障：保活连接正常。");

            await AwaitMainThread();
            Diag.Log("[Checkpoint] 阶段1屏障通过（无在场客机需协调，客机将随后独立重连）。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 阶段1屏障校验异常（忽略，继续重载）：{ex.Message}");
        }
    }

    /// <summary>
    /// ③ 阶段2 屏障（两阶段 LoadBarrier 的主机侧等效）。
    /// QuickSL 此处跨端等待「所有客机同步器注册就绪」；AR 无在场客机，故退化为
    /// 「新 run 同步器就绪校验 + 让出一帧」，在 <c>LoadRun</c> 真正开始 tick 前确认
    /// <c>SetUpSavedMultiplayer</c> 成功建立了战斗/事件/一次性/输入同步器。
    /// </summary>
    private static async Task HostLoadBarrierPhase2Async(RunManager rm)
    {
        try
        {
            var missing = new List<string>();
            if (rm.CombatStateSynchronizer == null) missing.Add("CombatStateSynchronizer");
            if (rm.EventSynchronizer == null) missing.Add("EventSynchronizer");
            if (rm.OneOffSynchronizer == null) missing.Add("OneOffSynchronizer");
            if (rm.InputSynchronizer == null) missing.Add("InputSynchronizer");

            if (missing.Count > 0)
                Diag.Log($"[Checkpoint] 阶段2屏障：警告——新 run 缺失同步器：{string.Join(", ", missing)}（SetUpSavedMultiplayer 可能未完全生效）。");
            else
                Diag.Log("[Checkpoint] 阶段2屏障：新 run 4 个网络同步器均已注册。");

            await AwaitMainThread();
            Diag.Log("[Checkpoint] 阶段2屏障通过，准备 LoadRun。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 阶段2屏障校验异常（忽略，继续 LoadRun）：{ex.Message}");
        }
    }

    #endregion

    /// <summary>跳回主线程一帧（Godot 信号驱动的 await，保证在主线兑现）。</summary>
    private static async Task AwaitMainThread()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
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
