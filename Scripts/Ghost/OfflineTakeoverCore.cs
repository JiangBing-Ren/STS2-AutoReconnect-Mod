using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoReconnect.Scripts.Ghost;

/// <summary>
/// v0.3.0 — Host 端离线接管核心（移植自 DirectConnectIP 的 OfflineTakeoverCore）。
/// 目的：让 AutoReconnect 自包含，长断线时无需同时安装 DirectConnectIP 也能保住对局。
/// 当某远程玩家掉线，8 秒宽限后进入 Ghost 状态，自动推进其回合/选择，使对局不冻结、不被放弃。
/// 掉线玩家在 8 秒内重连（由 ReconnectRunner 客户端逻辑完成）则无缝接手（Ghost 未推进状态）。
/// </summary>
internal static class OfflineTakeoverCore
{
    private static readonly MethodInfo EnqueueActionMethod = AccessTools.Method(typeof(ActionQueueSynchronizer), "EnqueueAction");
    private static readonly MethodInfo HostBroadcastMessageMethod = AccessTools.Method(typeof(NetHostGameService), "BroadcastMessage");
    /// <summary>接管宽限时间（毫秒）。改为可运行时配置，供 ModConfig 设置界面调整。
    /// v0.6.5 — 默认 30s。必须大于客机重连到达时间（Steam ~15s 超时 + 重连重试 5~10s ≈ 20~25s），
    /// 否则主机会在客机重连握手抵达前就接管并推进战斗，导致客机重连被 RunInProgress 拒绝（"加入失败"）。
    /// 仅当客机确实在宽限期内未重连，主机才接管其回合。可在 ModConfig 的 takeoverDelayMs 滑块继续上调（上限 60s）。</summary>
    public static ulong OfflineTakeoverDelayMs = 30_000;
    private static readonly Dictionary<ulong, OfflinePeerState> OfflinePeers = [];
    private static readonly object OfflinePeersLock = new();
    private static readonly HashSet<string> ScheduledRetries = [];
    private static readonly object ScheduledRetriesLock = new();
    private static readonly HashSet<ulong> LoadedRunOfflinePlayerIds = [];
    private static readonly object LoadedRunOfflinePlayerIdsLock = new();

    /// <summary>接管总开关（默认开启）。预留给后续设置界面。</summary>
    public static bool TakeoverEnabled { get; set; } = true;

    /// <summary>
    /// 接管是否处于活跃状态。
    /// AutoReconnect 自包含设计：只要当前处于多人对局（Host 或 Client）即自动激活，
    /// 不依赖 DirectConnectIP 或任何外部 mod 置位。
    /// </summary>
    public static bool IsTakeoverActive
    {
        get
        {
            if (!TakeoverEnabled) return false;
            try
            {
                return RunManager.Instance is { NetService: { } netService }
                       && netService.Type is NetGameType.Host or NetGameType.Client;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>接管总开关查询。</summary>
    public static bool IsTakeoverConfigEnabled() => TakeoverEnabled;

    private enum PeerTakeoverState
    {
        Online,
        Pending,
        Ghost
    }

    private sealed class OfflinePeerState
    {
        public ulong DisconnectedAtMsec;
        public bool TransportConnected;
        public ulong TransportConnectedAtMsec;
        public bool CombatTakeoverAdvanced;
        public bool TakeoverLogged;
        public NetError LastDisconnectReason;
    }

    public static bool IsGhost(ulong netId)
    {
        try
        {
            if (!IsTakeoverConfigEnabled()) return false;
            if (!IsTakeoverActive) return false;
            if (IsLocalNetId(netId)) return false;
            if (RunManager.Instance is not { NetService: { } netService }) return false;
            if (netService.Type is not (NetGameType.Host or NetGameType.Client)) return false;

            RefreshInferredPeerState();
            return GetPeerTakeoverState(netId, out _) == PeerTakeoverState.Ghost;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v0.6.5 — 绕过防抖的原始托管态查询：某 peer 是否已被标记为离线且已超过托管宽限
    /// （即处于 Ghost 态），不受 RefreshInferredPeerState 把"transport 仍连着"的僵尸翻回 Online 的影响。
    /// 用于检测"主机已托管(Ghost)、但客机 transport 仍连着"的非对称僵尸：这种僵尸
    /// IsGhost 会返回 false（被防抖判回 Online），必须用此原始检查。
    /// </summary>
    public static bool IsMarkedGhostRaw(ulong netId)
    {
        if (!IsTakeoverConfigEnabled()) return false;
        if (!IsTakeoverActive) return false;
        if (IsLocalNetId(netId)) return false;
        lock (OfflinePeersLock)
        {
            if (!OfflinePeers.TryGetValue(netId, out var st)) return false;
            if (st.DisconnectedAtMsec == 0) return false;
            return Time.GetTicksMsec() - st.DisconnectedAtMsec >= OfflineTakeoverDelayMs;
        }
    }

    public static bool IsOfflineOrPending(ulong netId)
    {
        try
        {
            if (!IsTakeoverConfigEnabled()) return false;
            if (!IsTakeoverActive) return false;
            if (IsLocalNetId(netId)) return false;
            if (RunManager.Instance is not { NetService: { } netService }) return false;
            if (netService.Type is not (NetGameType.Host or NetGameType.Client)) return false;

            RefreshInferredPeerState();
            return GetPeerTakeoverState(netId, out _) != PeerTakeoverState.Online;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPendingGhost(ulong netId, out ulong remainingMs)
    {
        remainingMs = 0;
        try
        {
            if (!IsTakeoverConfigEnabled()) return false;
            if (!IsTakeoverActive) return false;
            if (IsLocalNetId(netId)) return false;
            if (RunManager.Instance is not { NetService: { } netService }) return false;
            if (netService.Type is not (NetGameType.Host or NetGameType.Client)) return false;

            RefreshInferredPeerState();
            return GetPeerTakeoverState(netId, out remainingMs) == PeerTakeoverState.Pending;
        }
        catch
        {
            remainingMs = 0;
            return false;
        }
    }

    public static bool HasPendingGhost(IEnumerable<ulong> netIds, out ulong retryDelayMs)
    {
        retryDelayMs = 0;
        var hasPending = false;
        var minRemainingMs = ulong.MaxValue;

        foreach (var netId in netIds)
        {
            if (!IsPendingGhost(netId, out var remainingMs)) continue;

            hasPending = true;
            if (remainingMs < minRemainingMs)
                minRemainingMs = remainingMs;
        }

        if (!hasPending) return false;

        retryDelayMs = Math.Clamp(minRemainingMs + 150UL, 250UL, OfflineTakeoverDelayMs + 250UL);
        return true;
    }

    private static PeerTakeoverState GetPeerTakeoverState(ulong netId, out ulong remainingMs)
    {
        remainingMs = 0;
        lock (OfflinePeersLock)
        {
            if (!OfflinePeers.TryGetValue(netId, out var state))
                return PeerTakeoverState.Online;

            if (state.DisconnectedAtMsec == 0)
                return PeerTakeoverState.Online;

            if (state.TransportConnected)
                return PeerTakeoverState.Online;

            var elapsed = Time.GetTicksMsec() - state.DisconnectedAtMsec;
            if (elapsed < OfflineTakeoverDelayMs)
            {
                remainingMs = OfflineTakeoverDelayMs - elapsed;
                return PeerTakeoverState.Pending;
            }

            if (!state.TakeoverLogged)
            {
                state.TakeoverLogged = true;
                Log.Warn($"[AutoReconnect] 玩家 {netId} 已确认断开 {elapsed}ms，进入离线托管判定。");
            }

            return PeerTakeoverState.Ghost;
        }
    }

    public static void MarkPeerDisconnected(ulong netId, NetError reason)
    {
        MarkPeerDisconnectedCore(netId, reason, preserveExistingDisconnectTime: reason == NetError.RunInProgress);
    }

    public static void MarkPeerDisconnectedImmediate(ulong netId, NetError reason, string context)
    {
        MarkPeerDisconnectedCore(netId, reason, preserveExistingDisconnectTime: false, immediateTakeover: true, context: context);
    }

    private static void MarkPeerDisconnectedCore(
        ulong netId,
        NetError reason,
        bool preserveExistingDisconnectTime,
        bool immediateTakeover = false,
        string? context = null)
    {
        if (!IsTakeoverConfigEnabled()) return;
        if (IsLocalNetId(netId)) return;

        lock (OfflinePeersLock)
        {
            var wasTracked = OfflinePeers.TryGetValue(netId, out var existing);
            var wasTransportConnected = existing?.TransportConnected == true;
            var state = existing;
            if (!wasTracked || state == null)
            {
                state = new OfflinePeerState();
                OfflinePeers[netId] = state;
            }

            if (immediateTakeover)
            {
                var now = Time.GetTicksMsec();
                state.DisconnectedAtMsec = now > OfflineTakeoverDelayMs ? now - OfflineTakeoverDelayMs : 1UL;
                state.TakeoverLogged = false;
            }
            else if (!preserveExistingDisconnectTime || state.DisconnectedAtMsec == 0 || state.TransportConnected)
            {
                state.DisconnectedAtMsec = Time.GetTicksMsec();
                state.TakeoverLogged = false;
            }

            state.TransportConnected = false;
            state.LastDisconnectReason = reason;

            if (immediateTakeover)
            {
                Log.Warn($"[AutoReconnect] 玩家 {netId} 已在存档载入时判定为离线，立即允许托管。原因: {reason} {context}");
            }
            else if (!wasTracked || wasTransportConnected)
            {
                Log.Warn($"[AutoReconnect] 玩家 {netId} 已标记为离线，等待 {OfflineTakeoverDelayMs}ms 后允许托管。原因: {reason}");
            }
        }
    }

    public static void MarkPeerRejoined(ulong netId)
    {
        lock (OfflinePeersLock)
        {
            OfflinePeers.Remove(netId);
        }

        lock (LoadedRunOfflinePlayerIdsLock)
        {
            LoadedRunOfflinePlayerIds.Remove(netId);
        }
    }

    private static void MarkPeerTransportConnected(ulong netId)
    {
        if (!IsTakeoverConfigEnabled()) return;
        if (IsLocalNetId(netId)) return;

        lock (OfflinePeersLock)
        {
            if (!OfflinePeers.TryGetValue(netId, out var state))
                return;

            state.TransportConnected = true;
            state.TransportConnectedAtMsec = Time.GetTicksMsec();
            state.TakeoverLogged = false;
        }
    }

    private static void MarkTakeoverAdvanced(ulong netId)
    {
        lock (OfflinePeersLock)
        {
            if (!OfflinePeers.TryGetValue(netId, out var state))
            {
                state = new OfflinePeerState { DisconnectedAtMsec = Time.GetTicksMsec() };
                OfflinePeers[netId] = state;
            }

            if (CombatManager.Instance.IsInProgress)
                state.CombatTakeoverAdvanced = true;
        }
    }

    private static void RefreshInferredPeerState()
    {
        if (!IsTakeoverConfigEnabled()) return;
        if (!IsTakeoverActive) return;
        if (RunManager.Instance is not { NetService: { } netService } runManager) return;
        if (!runManager.IsInProgress) return;

        // 战斗结束后清掉托管推进标记，让掉线玩家在下一场战斗前能顺利重连
        ClearAllCombatTakeoverFlagsIfCombatEnded();

        var runState = runManager.DebugOnlyGetState();
        if (runState?.Players == null || runState.Players.Count <= 1) return;

        var onlineIds = GetObservedOnlineIds(netService);
        foreach (var player in runState.Players)
        {
            var playerId = player.NetId;
            if (IsLocalNetId(playerId)) continue;

            // 防抖（关键修复）：一旦某 peer 已被显式标记为掉线（DisconnectedAtMsec 已置位且
            // 尚未被 MarkPeerTransportConnected 重新连上），就不要因为 ConnectedPeers 仍残留该
            // peer（Steam P2P 断开有网络延迟，传输层不会瞬间移除）而把它“翻回在线”。
            // 否则 IsGhost/IsOfflineOrPending 会在 online/offline 之间剧烈抖动，导致：
            //   - CombatTurnEndTakeoverPatch 等补丁时灵时不灵；
            //   - 敌人回合所需的“所有玩家已准备开始敌人回合”(_playersReadyToBeginEnemyTurn 集齐)
            //     永远无法满足 → AfterAllPlayersReadyToBeginEnemyTurn 不触发 → 敌人回合不开始 →
            //     表现为“客机跳过了回合，但游戏没有实际进入下一回合”。
            // 真正的“重新上线”只通过 MarkPeerRejoined（收到 PlayerRejoinedMessage）清除离线标记，
            // 届时本函数会重新按 ConnectedPeers 推断为在线。
            bool alreadyMarkedOffline;
            lock (OfflinePeersLock)
                alreadyMarkedOffline = OfflinePeers.TryGetValue(playerId, out var st)
                                        && st.DisconnectedAtMsec != 0 && !st.TransportConnected;
            if (alreadyMarkedOffline) continue;

            if (onlineIds.Contains(playerId))
                MarkPeerTransportConnected(playerId);
            else
                MarkPeerDisconnectedCore(playerId, NetError.Quit, preserveExistingDisconnectTime: true);
        }
    }

    /// <summary>
    /// 是否应拒绝运行中重连。
    ///
    /// 【与 DirectConnectIP 的关键差异】
    /// DCI 的策略是"只要进过房间就一律拒绝运行中重连"，因为它的目标只是让对局别卡住，
    /// 掉线玩家回不回得来并不重要。AutoReconnect 的首要目标恰恰相反 —— 必须让掉线玩家回来。
    /// 因此这里默认全部放行，只保留一条真正会导致状态错乱的红线：
    ///   Ghost 已经代掉线玩家推进过战斗动作，且该场战斗仍在进行中。
    /// 此时重连者本地的战斗快照与主机已不一致，直接接入会崩；等本场战斗结束即可重连。
    /// </summary>
    public static bool ShouldRejectRunningRejoin(ulong netId, out NetError reason, out string detail)
    {
        reason = NetError.RunInProgress;
        detail = string.Empty;

        if (!IsTakeoverConfigEnabled()) return false;
        if (!IsTakeoverActive) return false;

        bool combatAdvanced;
        lock (OfflinePeersLock)
        {
            combatAdvanced = OfflinePeers.TryGetValue(netId, out var state) && state.CombatTakeoverAdvanced;
        }

        if (!combatAdvanced) return false;

        var combatInProgress = false;
        try
        {
            combatInProgress = CombatManager.Instance is { IsInProgress: true };
        }
        catch
        {
            // CombatManager 尚未就绪 —— 视为不在战斗中，放行
        }

        if (!combatInProgress)
        {
            // 战斗已结束，托管推进的状态不再影响重连，清除标记并放行
            ClearCombatTakeoverFlag(netId);
            return false;
        }

        detail = "战斗托管已代该玩家推进过战斗状态，需等本场战斗结束后再重连。";
        return true;
    }

    private static void ClearCombatTakeoverFlag(ulong netId)
    {
        lock (OfflinePeersLock)
        {
            if (OfflinePeers.TryGetValue(netId, out var state))
                state.CombatTakeoverAdvanced = false;
        }
    }

    /// <summary>战斗结束时清除全部托管推进标记，避免影响下一场战斗的重连判定。</summary>
    private static void ClearAllCombatTakeoverFlagsIfCombatEnded()
    {
        try
        {
            if (CombatManager.Instance is { IsInProgress: true }) return;
        }
        catch
        {
            // 忽略
        }

        lock (OfflinePeersLock)
        {
            foreach (var state in OfflinePeers.Values)
                state.CombatTakeoverAdvanced = false;
        }
    }

    public static IReadOnlyList<ulong> RememberLoadedRunMissingPlayers(
        SerializableRun run,
        IEnumerable<ulong> connectedPlayerIds,
        string context)
    {
        if (!IsTakeoverConfigEnabled()) return Array.Empty<ulong>();
        if (!IsTakeoverActive) return Array.Empty<ulong>();
        if (run?.Players == null) return Array.Empty<ulong>();

        var connected = connectedPlayerIds?.ToHashSet() ?? [];
        AddLocalObservedId(connected);

        if (RunManager.Instance is { NetService: { } netService })
        {
            connected.Add(netService.NetId);
            if (netService is NetClientGameService clientService)
                connected.Add(clientService.HostNetId);
        }

        var missing = new List<ulong>();
        foreach (var player in run.Players)
        {
            var netId = player.NetId;
            if (IsLocalNetId(netId)) continue;
            if (connected.Contains(netId))
            {
                MarkPeerRejoined(netId);
                continue;
            }

            missing.Add(netId);
            MarkPeerDisconnectedImmediate(netId, NetError.Quit, context);
        }

        if (missing.Count == 0) return missing;

        lock (LoadedRunOfflinePlayerIdsLock)
        {
            foreach (var netId in missing)
                LoadedRunOfflinePlayerIds.Add(netId);
        }

        return missing;
    }

    public static void ApplyLoadedRunOfflinePlayersToRunLobby(RunLobby runLobby, RunState state)
    {
        if (!IsTakeoverConfigEnabled()) return;
        if (!IsTakeoverActive) return;
        if (runLobby == null || state?.Players == null) return;

        HashSet<ulong> offlineIds;
        lock (LoadedRunOfflinePlayerIdsLock)
        {
            offlineIds = LoadedRunOfflinePlayerIds.ToHashSet();
        }

        if (offlineIds.Count == 0) return;

        var runPlayerIds = state.Players.Select(p => p.NetId).ToHashSet();

        // 注意：当前游戏版本的 RunLobby 没有独立的 "_connectedPlayerIds" 集合
        // （只有 List<RunLobbyPlayer> Players / IEnumerable<ulong> PlayerIds）。
        // 从 Players 里移除玩家会让存档中的对局成员消失，风险过高。
        // 因此这里只登记离线状态，Ghost 推进完全由 OfflinePeers 字典驱动，
        // 不触碰游戏自身的大厅集合 —— 更安全，且不影响后续重连。
        foreach (var netId in offlineIds)
        {
            if (!runPlayerIds.Contains(netId)) continue;

            Log.Warn($"[AutoReconnect] 存档载入时判定玩家 {netId} 离线，立即纳入托管。");
            MarkPeerDisconnectedImmediate(netId, NetError.Quit, "loaded-run-lobby");
        }

        lock (LoadedRunOfflinePlayerIdsLock)
        {
            LoadedRunOfflinePlayerIds.Clear();
        }
    }

    public static void EnqueueGhostAction(GameAction action, ulong ghostNetId)
    {
        if (!IsTakeoverConfigEnabled()) return;

        try
        {
            if (RunManager.Instance is not { ActionQueueSynchronizer: { } sync }) return;

            if (EnqueueActionMethod != null)
            {
                EnqueueActionMethod.Invoke(sync, [action, ghostNetId]);
                MarkTakeoverAdvanced(ghostNetId);
            }
            else
            {
                Log.Error("[AutoReconnect] 找不到 EnqueueAction 方法，代管发包失败！");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoReconnect] 动作 {action} 发包失败: {ex}");
        }
    }

    public static bool BroadcastGhostMessageToClients<TMessage>(TMessage message, ulong ghostNetId)
    {
        if (!IsTakeoverConfigEnabled()) return false;

        try
        {
            if (RunManager.Instance is not { NetService: NetHostGameService hostService }) return false;
            if (HostBroadcastMessageMethod == null)
            {
                Log.Error("[AutoReconnect] 找不到 NetHostGameService.BroadcastMessage，幽灵事件同步失败！");
                return false;
            }

            var messageType = message?.GetType();
            if (messageType == null) return false;

            var method = HostBroadcastMessageMethod.IsGenericMethod
                ? HostBroadcastMessageMethod.MakeGenericMethod(messageType)
                : HostBroadcastMessageMethod;
            method.Invoke(hostService, [message, ghostNetId, 0, ghostNetId]);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoReconnect] 幽灵玩家 {ghostNetId} 消息同步失败: {ex}");
            return false;
        }
    }

    public static void ScheduleTakeoverRetry(object owner, string reason, ulong delayMs, Action callback)
    {
        if (!IsTakeoverConfigEnabled()) return;
        if (!IsTakeoverActive) return;

        var key = $"{RuntimeHelpers.GetHashCode(owner)}:{reason}";
        lock (ScheduledRetriesLock)
        {
            if (!ScheduledRetries.Add(key)) return;
        }

        var context = SynchronizationContext.Current;
        _ = RunScheduledRetryAsync(key, delayMs, context, callback);
    }

    private static async Task RunScheduledRetryAsync(string key, ulong delayMs, SynchronizationContext? context, Action callback)
    {
        try
        {
            await Task.Delay((int)Math.Min(delayMs, int.MaxValue));
            if (context != null)
                context.Post(_ => callback(), null);
            else
                callback();
        }
        catch
        {
            // ignore
        }
        finally
        {
            lock (ScheduledRetriesLock)
            {
                ScheduledRetries.Remove(key);
            }
        }
    }

    private static HashSet<ulong> GetObservedOnlineIds(INetGameService netService)
    {
        var ids = new HashSet<ulong>();

        if (LocalContext.NetId.HasValue)
            ids.Add(LocalContext.NetId.Value);

        switch (netService)
        {
            case NetHostGameService hostService:
                ids.Add(hostService.NetId);
                foreach (var peer in hostService.ConnectedPeers)
                {
                    if (peer.readyForBroadcasting)
                        ids.Add(peer.peerId);
                }
                break;
            case NetClientGameService clientService:
                ids.Add(clientService.NetId);
                ids.Add(clientService.HostNetId);
                if (RunManager.Instance.RunLobby is RunLobby runLobby)
                {
                    foreach (var id in runLobby.PlayerIds)
                        ids.Add(id);
                }
                break;
        }

        return ids;
    }

    private static HashSet<ulong> GetBroadcastReadyIds()
    {
        var ids = new HashSet<ulong>();

        if (LocalContext.NetId.HasValue)
            ids.Add(LocalContext.NetId.Value);

        if (RunManager.Instance is not { NetService: { IsConnected: true } netService })
            return ids;

        if (netService.Type == NetGameType.Host && netService is NetHostGameService hostService)
        {
            foreach (var peer in hostService.ConnectedPeers)
            {
                if (peer.readyForBroadcasting)
                    ids.Add(peer.peerId);
            }
        }
        else if (netService.Type == NetGameType.Client && RunManager.Instance.RunLobby is RunLobby runLobby)
        {
            foreach (var id in runLobby.PlayerIds)
                ids.Add(id);
        }

        return ids;
    }

    private static void AddLocalObservedId(HashSet<ulong> ids)
    {
        if (LocalContext.NetId.HasValue)
            ids.Add(LocalContext.NetId.Value);
    }

    private static bool IsLocalNetId(ulong netId)
    {
        return LocalContext.NetId == netId;
    }
}
