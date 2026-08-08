using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoReconnect.Scripts.Ghost;

[HarmonyPatch(typeof(RunLobby), "HandleClientRejoinRequestMessage")]
public static class RunningRejoinGuardPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static bool Prefix(ClientRejoinRequestMessage message, ulong senderId, out bool __state)
    {
        __state = false;

        // v0.7.1 —— 第一件事：告诉僵尸看门狗「这个 peer 走的是正规重连握手」。
        // 否则重连者在握手期间仍处 Ghost 原始态，会被看门狗当成 Steam 静默重连的僵尸强断，
        // 客机侧就看到 1016 Application closed connection → 最终报「连接超时」。
        // 这是 1016 真凶的第二道保险（第一道是把 OnPeerConnected 的立即强断改成宽限期）。
        HostZombieWatchdog.NoteRejoinHandshake(senderId);

        if (!OfflineTakeoverCore.IsTakeoverActive) return true;
        if (!OfflineTakeoverCore.ShouldRejectRunningRejoin(senderId, out var reason, out var detail))
        {
            __state = RunManager.Instance.DebugOnlyGetState()?.Players.Any(p => p.NetId == senderId) == true;
            return true;
        }

        Log.Warn($"[AutoReconnect] 拒绝玩家 {senderId} 运行中重连：{detail}");
        ReconnectDiagnostics.ReportHostEvent(
            HostReconnectEvent.ClientRejoinRejected,
            senderId,
            $"{detail}\n（本场战斗已由主机代打推进，中途接回会导致双方战斗状态分歧。战斗结束后即可重连。）");

        if (RunManager.Instance.NetService is NetHostGameService hostService)
        {
            hostService.DisconnectClient(senderId, reason);
        }

        return false;
    }

    public static void Postfix(ulong senderId, bool __state)
    {
        if (!__state) return;
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (OfflineTakeoverCore.ShouldRejectRunningRejoin(senderId, out _, out _)) return;

        OfflineTakeoverCore.MarkPeerRejoined(senderId);
        ReconnectDiagnostics.ReportHostEvent(
            HostReconnectEvent.ClientRejoinAccepted,
            senderId,
            "重连握手通过，已解除离线托管，该玩家重新接管自己的角色。");
    }
}

[HarmonyPatch(typeof(RunLobby), "HandlePlayerLeftMessage")]
public static class RunLobbyPeerLeftTakeoverStatePatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(PlayerLeftMessage message)
    {
        OfflineTakeoverCore.MarkPeerDisconnected(message.playerId, NetError.Quit);
        OfflineTakeoverPoke.ScheduleAfterDisconnect(message.playerId);
    }
}

// ==========================================
// Bug C 修复（v0.6.1）：主机端超时断线的接管入口
// ==========================================
// 长断线（传输层超时，约 14s）时，游戏走 RunLobby.OnDisconnectedFromClientAsHost(playerId, info)，
// 而不是 HandlePlayerLeftMessage —— 后者是"消息处理器"，主机把 PlayerLeftMessage 广播给客户端，
// 自己并不会收到、也就不会调用 HandlePlayerLeftMessage。因此原先只 patch HandlePlayerLeftMessage
// 的 RunLobbyPeerLeftTakeoverStatePatch 在主机超时断线时根本不触发：
//   → MarkPeerDisconnected + ScheduleAfterDisconnect 从未在主机执行
//   → 没有任何 poke 去重新评估 Ghost 状态；而战斗事件也不会再触发
//     （掉线玩家永远不会再 ready，SetReadyToEndTurn / SetReadyToBeginEnemyTurn 不再为其调用）
//   → "所有玩家已准备开始敌人回合"永远集不齐 → 敌人回合不开始
//   → 表现为"主机视角下客机回合被跳过，但游戏没有真正进入下一回合"（正是用户报告的现象）。
// 这里补上对 OnDisconnectedFromClientAsHost 的 Postfix：主机超时断线时立即启动接管倒计时并调度 poke，
// 让接管重试链把状态推进到 Ghost 接手，回合正常前进。
[HarmonyPatch(typeof(RunLobby), "OnDisconnectedFromClientAsHost")]
public static class RunLobbyHostDisconnectTakeoverStatePatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(ulong playerId, NetErrorInfo info)
    {
        NetError reason;
        try { reason = info.GetReason(); }
        catch { reason = NetError.Quit; }

        // v0.6.5 — RunInProgress 是主机自己拒绝"运行中重连"所导致的断线（战斗托管已代该玩家推进过战斗状态），
        // 并非真实掉线。若据此重启托管倒计时，会与 RunInProgress 拒绝形成反复倒计时循环
        // （每次拒绝→断线→再倒计时），并干扰已有的正常 Ghost 状态。直接忽略：
        // 该 peer 的离线标记已由首次 Timeout 断线建立，无需重复处理。
        if (reason == NetError.RunInProgress)
        {
            Log.Warn($"[AutoReconnect] 忽略 {playerId} 的 RunInProgress 断线（主机拒绝运行中重连所致），不重启托管倒计时。");
            return;
        }

        Log.Warn($"[AutoReconnect] 主机检测到玩家 {playerId} 超时断线（OnDisconnectedFromClientAsHost，原因 {reason}），启动离线托管倒计时。");
        ReconnectDiagnostics.ReportHostEvent(
            HostReconnectEvent.ClientDisconnected,
            playerId,
            $"{ReconnectDiagnostics.DescribeNetError(reason)}。" +
            $"主机将在 {OfflineTakeoverCore.OfflineTakeoverDelayMs / 1000} 秒后接管其角色，对局不会卡住；对方重连成功会自动交还控制权。");
        OfflineTakeoverCore.MarkPeerDisconnected(playerId, reason);
        OfflineTakeoverPoke.ScheduleAfterDisconnect(playerId);
    }
}

[HarmonyPatch(typeof(RunLobby), "HandlePlayerRejoinedMessage")]
public static class RunLobbyPeerRejoinedTakeoverStatePatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    // 当前游戏版本：PlayerRejoinedMessage 携带 RunLobbyPlayer player（其 NetId 字段名为 id）
    public static void Postfix(PlayerRejoinedMessage message)
    {
        OfflineTakeoverCore.MarkPeerRejoined(message.player.id);
    }
}

[HarmonyPatch(typeof(LoadRunLobby), "HandlePlayerLeftMessage")]
public static class LoadRunLobbyPeerLeftTakeoverStatePatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(PlayerLeftMessage message)
    {
        OfflineTakeoverCore.MarkPeerDisconnectedImmediate(message.playerId, NetError.Quit, "load-lobby-left");
    }
}

[HarmonyPatch(typeof(LoadRunLobby), "HandlePlayerReconnectedMessage")]
public static class LoadRunLobbyPeerRejoinedTakeoverStatePatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    // 当前游戏版本：PlayerReconnectedMessage 携带 LoadRunLobbyPlayer player（其 NetId 字段名为 id）
    public static void Postfix(PlayerReconnectedMessage message)
    {
        OfflineTakeoverCore.MarkPeerRejoined(message.player.id);
    }
}

[HarmonyPatch(typeof(LoadRunLobby), "TryBeginRunForAllPlayers")]
public static class LoadRunLobbyOfflinePlayersBeforeBeginPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Prefix(LoadRunLobby __instance)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (__instance.NetService.Type != NetGameType.Host) return;

        var missingPlayers = OfflineTakeoverCore.RememberLoadedRunMissingPlayers(
            __instance.Run,
            __instance.PlayerIds,
            "host-load-run-begin");
        if (missingPlayers.Count == 0) return;

        foreach (var playerId in missingPlayers)
        {
            __instance.NetService.SendMessage(new PlayerLeftMessage { playerId = playerId });
        }
    }
}

[HarmonyPatch(typeof(LoadRunLobby), "BeginRunLocally")]
public static class LoadRunLobbyOfflinePlayersLocalBeginPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Prefix(LoadRunLobby __instance)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;

        OfflineTakeoverCore.RememberLoadedRunMissingPlayers(
            __instance.Run,
            __instance.PlayerIds,
            "load-run-local-begin");
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeRunLobby")]
public static class LoadedRunOfflinePlayersRunLobbyPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(RunManager __instance, RunState state)
    {
        if (__instance.RunLobby is not { } runLobby) return;
        OfflineTakeoverCore.ApplyLoadedRunOfflinePlayersToRunLobby(runLobby, state);
    }
}

public static class OfflineTakeoverPoke
{
    public static void ScheduleAfterDisconnect(ulong netId)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;

        var retryDelayMs = OfflineTakeoverCore.IsPendingGhost(netId, out var remainingMs)
            ? remainingMs + 150UL
            : 250UL;
        OfflineTakeoverCore.ScheduleTakeoverRetry(typeof(OfflineTakeoverPoke), $"disconnect:{netId}", retryDelayMs, PokeCurrentSynchronizers);
    }

    private static void PokeCurrentSynchronizers()
    {
        try
        {
            if (!OfflineTakeoverCore.IsTakeoverActive) return;
            if (RunManager.Instance is not { NetService: { Type: NetGameType.Host } } runManager) return;

            var runState = runManager.DebugOnlyGetState();
            var player = runState?.Players.FirstOrDefault(p => !OfflineTakeoverCore.IsOfflineOrPending(p.NetId))
                         ?? runState?.Players.FirstOrDefault();
            var roomType = runState?.CurrentRoom?.RoomType ?? RoomType.Unassigned;

            var actionQueueSynchronizer = runManager.ActionQueueSynchronizer;
            if (actionQueueSynchronizer != null && CombatManager.Instance.IsInProgress)
            {
                AutoReadyEnemyTurnPatch.Postfix(actionQueueSynchronizer, actionQueueSynchronizer.CombatState);
            }

            if (player != null && CombatManager.Instance.IsInProgress)
            {
                CombatTurnEndTakeoverPatch.Postfix(CombatManager.Instance, player);
                SetReadyToBeginEnemyTakeoverPatch.Postfix(CombatManager.Instance, player);
            }

            if (roomType == RoomType.Map && runManager.MapSelectionSynchronizer != null)
            {
                MapSelectionGhostPatch.Postfix(runManager.MapSelectionSynchronizer);
            }

            if (player != null && roomType == RoomType.Treasure && runManager.TreasureRoomRelicSynchronizer != null)
            {
                TreasureUnblockPatch.Postfix(runManager.TreasureRoomRelicSynchronizer, player);
            }

            if (player != null && roomType == RoomType.Boss && runManager.ActChangeSynchronizer != null)
            {
                ActChangeGhostPatch.Postfix(runManager.ActChangeSynchronizer, player);
            }

            if (runManager.CombatStateSynchronizer != null)
            {
                CombatSyncTakeoverPatch.Postfix(runManager.CombatStateSynchronizer);
            }

            if (roomType == RoomType.Event && runManager.EventSynchronizer != null)
            {
                EventUnblockPatch.Postfix(runManager.EventSynchronizer);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[AutoReconnect] 离线托管状态刷新失败: {ex}");
        }
    }
}

// ==========================================
// 战斗托管：自动结束回合 / 准备敌人阶段（轻量 Ghost，不出牌）
// ==========================================

/// <summary>
/// 访问 CombatManager 内部的"已准备进入敌人回合"玩家集合。
/// 该集合位于 CombatManager._turnState（CombatTurnState，internal）的
/// PlayersReadyToBeginEnemyTurn 属性（HashSet&lt;Player&gt;）。
/// v0.6.1 错误地假设字段名为 CombatManager._playersReadyToBeginEnemyTurn，
/// 该字段不存在导致反射得到 null，补丁静默空转、敌人回合永不开始。
/// </summary>
internal static class CombatTurnStateAccess
{
    private static readonly FieldInfo? TurnStateField = AccessTools.Field(typeof(CombatManager), "_turnState");
    private static PropertyInfo? _readyPlayersProp;

    public static HashSet<Player>? GetReadyPlayers(CombatManager cm)
    {
        if (cm == null) return null;
        var turnState = TurnStateField?.GetValue(cm);
        if (turnState == null) return null;
        _readyPlayersProp ??= AccessTools.Property(turnState.GetType(), "PlayersReadyToBeginEnemyTurn");
        return _readyPlayersProp?.GetValue(turnState) as HashSet<Player>;
    }
}

[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.SetCombatState))]
public static class AutoReadyEnemyTurnPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(ActionQueueSynchronizer __instance, ActionSynchronizerCombatState combatState)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;
        if (combatState != ActionSynchronizerCombatState.EndTurnPhaseOne) return;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null) return;

        var readySet = CombatTurnStateAccess.GetReadyPlayers(CombatManager.Instance);
        if (OfflineTakeoverCore.HasPendingGhost(state.Players.Select(p => p.NetId), out var retryDelayMs))
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(AutoReadyEnemyTurnPatch), retryDelayMs, () =>
            {
                if (!ReferenceEquals(RunManager.Instance.ActionQueueSynchronizer, __instance)) return;
                if (__instance.CombatState != ActionSynchronizerCombatState.EndTurnPhaseOne) return;
                Postfix(__instance, ActionSynchronizerCombatState.EndTurnPhaseOne);
            });
        }

        foreach (var p in state.Players)
        {
            if (!OfflineTakeoverCore.IsGhost(p.NetId) || !p.Creature.IsAlive) continue;
            if (readySet == null || readySet.Contains(p)) continue;

            OfflineTakeoverCore.EnqueueGhostAction(new ReadyToBeginEnemyTurnAction(p), p.NetId);
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
public static class CombatTurnEndTakeoverPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(CombatManager __instance, Player player)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;

        var state = __instance.DebugOnlyGetState();
        if (state == null || !__instance.IsInProgress || state.CurrentSide != CombatSide.Player) return;
        if (state.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;

        var allOnlineReady = state.Players.Where(p => !OfflineTakeoverCore.IsOfflineOrPending(p.NetId) && p.Creature.IsAlive).All(__instance.IsPlayerReadyToEndTurn);
        if (!allOnlineReady) return;

        if (OfflineTakeoverCore.HasPendingGhost(state.Players.Select(p => p.NetId), out var retryDelayMs))
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(CombatTurnEndTakeoverPatch), retryDelayMs, () =>
            {
                if (!CombatManager.Instance.IsInProgress) return;
                Postfix(__instance, player);
            });
            return;
        }

        var roundNumber = state.RoundNumber;
        foreach (var p in state.Players)
        {
            if (!OfflineTakeoverCore.IsGhost(p.NetId) || !p.Creature.IsAlive || __instance.IsPlayerReadyToEndTurn(p)) continue;
            OfflineTakeoverCore.EnqueueGhostAction(new EndPlayerTurnAction(p, roundNumber), p.NetId);
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToBeginEnemyTurn))]
public static class SetReadyToBeginEnemyTakeoverPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(CombatManager __instance, Player player)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;

        if (CombatTurnStateAccess.GetReadyPlayers(__instance) is not HashSet<Player> readySet) return;

        var state = __instance.DebugOnlyGetState();
        if (state == null) return;
        if (state.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;

        var allOnlineReady = state.Players.Where(p => !OfflineTakeoverCore.IsOfflineOrPending(p.NetId)).All(p => readySet.Contains(p));
        if (!allOnlineReady) return;

        if (OfflineTakeoverCore.HasPendingGhost(state.Players.Select(p => p.NetId), out var retryDelayMs))
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(SetReadyToBeginEnemyTakeoverPatch), retryDelayMs, () =>
            {
                if (!CombatManager.Instance.IsInProgress) return;
                Postfix(__instance, player);
            });
            return;
        }

        foreach (var p in state.Players.Where(p => OfflineTakeoverCore.IsGhost(p.NetId)))
        {
            if (readySet.Contains(p)) continue;
            OfflineTakeoverCore.EnqueueGhostAction(new ReadyToBeginEnemyTurnAction(p), p.NetId);
        }
    }
}

// ==========================================
// 地图选择托管
// ==========================================
[HarmonyPatch(typeof(MapSelectionSynchronizer), nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
public static class MapSelectionGhostPatch
{
    private static readonly FieldInfo? VotesField = AccessTools.Field(typeof(MapSelectionSynchronizer), "_votes");
    private static readonly MethodInfo? MoveMethod = AccessTools.Method(typeof(MapSelectionSynchronizer), "MoveToMapCoord");

    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(MapSelectionSynchronizer __instance)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;
        if (VotesField?.GetValue(__instance) is not IList votesList) return;

        var players = RunManager.Instance.DebugOnlyGetState()?.Players;
        if (players == null) return;

        if (players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;

        MapVote? fallbackVote = null;
        var allOnlineVoted = true;

        for (var i = 0; i < players.Count; i++)
        {
            if (i >= votesList.Count) break;
            if (OfflineTakeoverCore.IsOfflineOrPending(players[i].NetId)) continue;

            if (votesList[i] is not MapVote vote) allOnlineVoted = false;
            else fallbackVote = vote;
        }

        if (!allOnlineVoted || !fallbackVote.HasValue) return;
        if (OfflineTakeoverCore.HasPendingGhost(players.Select(p => p.NetId), out var retryDelayMs))
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(MapSelectionGhostPatch), retryDelayMs, () =>
            {
                if (!ReferenceEquals(RunManager.Instance.MapSelectionSynchronizer, __instance)) return;
                Postfix(__instance);
            });
            return;
        }

        var needsInvoke = false;
        for (var i = 0; i < votesList.Count; i++)
        {
            if ((votesList[i] as MapVote?).HasValue) continue;
            votesList[i] = fallbackVote;
            needsInvoke = true;
        }
        if (needsInvoke) MoveMethod?.Invoke(__instance, null);
    }
}

// ==========================================
// 遗物宝箱托管
// ==========================================
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.OnPicked))]
public static class TreasureUnblockPatch
{
    private static readonly FieldInfo? PlayerCollectionField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_playerCollection");
    private static readonly FieldInfo? VotesField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes");
    private static readonly FieldInfo? CurrentRelicsField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_currentRelics");

    private static FieldInfo? _voteReceivedField;
    private static FieldInfo? _voteIndexField;

    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(TreasureRoomRelicSynchronizer __instance, Player player)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;

        var playerCollection = PlayerCollectionField?.GetValue(__instance) as IPlayerCollection;
        var votesList = VotesField?.GetValue(__instance) as IList;
        var currentRelics = CurrentRelicsField?.GetValue(__instance) as IEnumerable;

        if (playerCollection == null || votesList == null || currentRelics == null) return;
        if (playerCollection.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;

        var relicCount = currentRelics.Cast<object>().Count();
        if (relicCount == 0) return;

        var hasPendingGhost = OfflineTakeoverCore.HasPendingGhost(playerCollection.Players.Select(p => p.NetId), out var retryDelayMs);
        var usedIndices = new HashSet<int>();

        for (var i = 0; i < playerCollection.Players.Count; i++)
        {
            if (i >= votesList.Count) break;

            var pId = playerCollection.Players[i].NetId;
            if (OfflineTakeoverCore.IsOfflineOrPending(pId)) continue;

            if (!HasVoted(votesList[i], out var vIndex)) return;
            if (vIndex.HasValue) usedIndices.Add(vIndex.Value);
        }

        if (hasPendingGhost)
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(TreasureUnblockPatch), retryDelayMs, () =>
            {
                if (!ReferenceEquals(RunManager.Instance.TreasureRoomRelicSynchronizer, __instance)) return;
                Postfix(__instance, player);
            });
            return;
        }

        for (var i = 0; i < playerCollection.Players.Count; i++)
        {
            if (i >= votesList.Count) break;

            var ghostPlayer = playerCollection.Players[i];
            if (!OfflineTakeoverCore.IsGhost(ghostPlayer.NetId) || HasVoted(votesList[i], out _)) continue;

            var pickIndex = -1;
            for (var j = 0; j < relicCount; j++)
            {
                if (usedIndices.Contains(j)) continue;
                pickIndex = j;
                usedIndices.Add(j);
                break;
            }

            if (pickIndex == -1) continue;
            GameAction pickAction;
            try
            {
                pickAction = (GameAction)Activator.CreateInstance(typeof(PickRelicAction), ghostPlayer, (int?)pickIndex)!;
            }
            catch
            {
                pickAction = (GameAction)Activator.CreateInstance(typeof(PickRelicAction), ghostPlayer, pickIndex)!;
            }

            OfflineTakeoverCore.EnqueueGhostAction(pickAction, ghostPlayer.NetId);
        }

        return;

        bool HasVoted(object? voteObj, out int? votedIndex)
        {
            votedIndex = null;
            if (voteObj == null) return false;

            if (_voteReceivedField == null)
            {
                var type = voteObj.GetType();
                _voteReceivedField = AccessTools.Field(type, "voteReceived");
                _voteIndexField = AccessTools.Field(type, "index");
            }

            if (_voteReceivedField != null)
            {
                var received = (bool)_voteReceivedField.GetValue(voteObj)!;
                votedIndex = _voteIndexField?.GetValue(voteObj) as int?;
                return received;
            }

            if (voteObj is not int intVote) return false;
            votedIndex = intVote;
            return true;
        }
    }
}

// ==========================================
// 关卡(章节)跳转托管
// ==========================================
[HarmonyPatch(typeof(ActChangeSynchronizer), nameof(ActChangeSynchronizer.OnPlayerReady))]
public static class ActChangeGhostPatch
{
    private static readonly FieldInfo? RunStateField = AccessTools.Field(typeof(ActChangeSynchronizer), "_runState");
    private static readonly FieldInfo? ReadyPlayersField = AccessTools.Field(typeof(ActChangeSynchronizer), "_readyPlayers");

    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(ActChangeSynchronizer __instance, Player player)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;
        if (RunStateField?.GetValue(__instance) is not RunState state) return;
        if (state.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;
        if (ReadyPlayersField?.GetValue(__instance) is not IList readyPlayers) return;

        var allOnlineReady = !state.Players.Where((t, i) => !OfflineTakeoverCore.IsOfflineOrPending(t.NetId) && (i >= readyPlayers.Count || !(bool)readyPlayers[i]!)).Any();
        if (!allOnlineReady) return;

        if (OfflineTakeoverCore.HasPendingGhost(state.Players.Select(p => p.NetId), out var retryDelayMs))
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(ActChangeGhostPatch), retryDelayMs, () =>
            {
                if (!ReferenceEquals(RunManager.Instance.ActChangeSynchronizer, __instance)) return;
                Postfix(__instance, player);
            });
            return;
        }

        for (var i = 0; i < state.Players.Count; i++)
        {
            if (i >= readyPlayers.Count) break;

            var ghostPlayer = state.Players[i];
            if (!OfflineTakeoverCore.IsGhost(ghostPlayer.NetId) || (bool)readyPlayers[i]!) continue;

            OfflineTakeoverCore.EnqueueGhostAction(new VoteToMoveToNextActAction(ghostPlayer, state.CurrentActIndex), ghostPlayer.NetId);
        }
    }
}

// ==========================================
// 战斗底层序列化状态同步托管
// ==========================================
[HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.StartSync))]
public static class CombatSyncTakeoverPatch
{
    private static readonly FieldInfo? NetServiceField = AccessTools.Field(typeof(CombatStateSynchronizer), "_netService");
    private static readonly FieldInfo? RunStateField = AccessTools.Field(typeof(CombatStateSynchronizer), "_runState");
    private static readonly FieldInfo? SyncDataField = AccessTools.Field(typeof(CombatStateSynchronizer), "_syncData");
    private static readonly MethodInfo? CheckSyncMethod = AccessTools.Method(typeof(CombatStateSynchronizer), "CheckSyncCompleted");

    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Postfix(CombatStateSynchronizer __instance)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (NetServiceField?.GetValue(__instance) is not INetGameService { Type: NetGameType.Host } netService) return;

        var runState = RunStateField?.GetValue(__instance) as RunState;
        var syncData = SyncDataField?.GetValue(__instance) as Dictionary<ulong, SerializablePlayer>;
        if (runState == null || syncData == null) return;

        if (runState.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;

        if (OfflineTakeoverCore.HasPendingGhost(runState.Players.Select(p => p.NetId), out var retryDelayMs))
        {
            OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(CombatSyncTakeoverPatch), retryDelayMs, () =>
            {
                if (!ReferenceEquals(RunManager.Instance.CombatStateSynchronizer, __instance)) return;
                Postfix(__instance);
            });
        }

        foreach (var ghostPlayer in runState.Players)
        {
            if (!OfflineTakeoverCore.IsGhost(ghostPlayer.NetId)) continue;

            var serializedGhost = ghostPlayer.ToSerializable();
            syncData[ghostPlayer.NetId] = serializedGhost;
            var message = new SyncPlayerDataMessage { player = serializedGhost };
            if (!OfflineTakeoverCore.BroadcastGhostMessageToClients(message, ghostPlayer.NetId))
            {
                netService.SendMessage(message);
            }
        }

        try
        {
            CheckSyncMethod?.Invoke(__instance, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
        }
    }
}

[HarmonyPatch(typeof(CombatStateSynchronizer), "OnSyncPlayerMessageReceived")]
public static class CombatSyncReceiveTakeoverPatch
{
    private static readonly FieldInfo? SyncDataField = AccessTools.Field(typeof(CombatStateSynchronizer), "_syncData");
    private static readonly MethodInfo? CheckSyncMethod = AccessTools.Method(typeof(CombatStateSynchronizer), "CheckSyncCompleted");

    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static bool Prefix(CombatStateSynchronizer __instance, SyncPlayerDataMessage syncMessage, ulong senderId)
    {
        if (!OfflineTakeoverCore.IsTakeoverConfigEnabled()) return true;
        if (!OfflineTakeoverCore.IsTakeoverActive) return true;
        if (RunManager.Instance.NetService.Type != NetGameType.Client) return true;

        var realPlayerId = syncMessage.player.NetId;
        if (realPlayerId == senderId) return true;

        var senderIsHost = RunManager.Instance.NetService is NetClientGameService clientService &&
                           senderId == clientService.HostNetId;
        if (!OfflineTakeoverCore.IsGhost(realPlayerId))
        {
            if (!senderIsHost) return true;
            OfflineTakeoverCore.MarkPeerDisconnectedImmediate(realPlayerId, NetError.Quit, "host-ghost-sync");
        }

        if (SyncDataField?.GetValue(__instance) is not Dictionary<ulong, SerializablePlayer> syncData) return false;

        syncData[realPlayerId] = syncMessage.player;

        try
        {
            CheckSyncMethod?.Invoke(__instance, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
        }

        return false;
    }
}

// ==========================================
// 事件房间托管
// ==========================================
[HarmonyPatch]
public static class EventUnblockPatch
{
    private static readonly FieldInfo? PlayerCollectionField = AccessTools.Field(typeof(EventSynchronizer), "_playerCollection");
    private static readonly PropertyInfo? IsSharedProperty = AccessTools.Property(typeof(EventSynchronizer), "IsShared");
    private static readonly FieldInfo? PlayerVotesField = AccessTools.Field(typeof(EventSynchronizer), "_playerVotes");
    private static readonly FieldInfo? PageIndexField = AccessTools.Field(typeof(EventSynchronizer), "_pageIndex");
    private static readonly MethodInfo? VoteMethod = AccessTools.Method(typeof(EventSynchronizer), "PlayerVotedForSharedOptionIndex");
    private static readonly FieldInfo? EventsField = AccessTools.Field(typeof(EventSynchronizer), "_events");
    private static readonly MethodInfo? ChooseMethod = AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForEvent");

    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    static IEnumerable<MethodBase> TargetMethods()
    {
        if (ChooseMethod != null) yield return ChooseMethod;
        if (VoteMethod != null) yield return VoteMethod;
    }

    public static void Postfix(EventSynchronizer __instance)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;

        var playerCollection = PlayerCollectionField?.GetValue(__instance) as IPlayerCollection;
        var isShared = IsSharedProperty != null && (bool)IsSharedProperty.GetValue(__instance)!;
        var runState = RunManager.Instance.DebugOnlyGetState();

        if (playerCollection == null || runState == null) return;
        if (playerCollection.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;
        var hasPendingGhost = OfflineTakeoverCore.HasPendingGhost(playerCollection.Players.Select(p => p.NetId), out var retryDelayMs);

        if (isShared)
        {
            var playerVotesObj = PlayerVotesField?.GetValue(__instance);
            if (playerVotesObj is not IList playerVotes) return;

            var allOnlineVoted = true;
            uint? fallbackVote = null;

            for (var i = 0; i < playerCollection.Players.Count; i++)
            {
                if (i >= playerVotes.Count) break;
                if (OfflineTakeoverCore.IsOfflineOrPending(playerCollection.Players[i].NetId)) continue;

                if (playerVotes[i] is not uint vote) { allOnlineVoted = false; break; }
                fallbackVote = vote;
            }

            if (!allOnlineVoted || !fallbackVote.HasValue) return;
            if (hasPendingGhost)
            {
                OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(EventUnblockPatch), retryDelayMs, () =>
                {
                    if (!ReferenceEquals(RunManager.Instance.EventSynchronizer, __instance)) return;
                    Postfix(__instance);
                });
                return;
            }

            for (var i = 0; i < playerCollection.Players.Count; i++)
            {
                if (i >= playerVotes.Count) break;
                if (!OfflineTakeoverCore.IsGhost(playerCollection.Players[i].NetId) || (playerVotes[i] as uint?).HasValue)
                    continue;

                var pageIndex = (uint)(PageIndexField?.GetValue(__instance) ?? 0U);
                VoteMethod?.Invoke(__instance, [playerCollection.Players[i], fallbackVote.Value, pageIndex]);
                OfflineTakeoverCore.BroadcastGhostMessageToClients(new VotedForSharedEventOptionMessage
                {
                    optionIndex = fallbackVote.Value,
                    pageIndex = pageIndex,
                    location = runState.RunLocation
                }, playerCollection.Players[i].NetId);
            }
        }
        else
        {
            if (EventsField?.GetValue(__instance) is not List<EventModel> events) return;

            var allOnlineFinished = !playerCollection.Players.Where((t, i) => i < events.Count && !OfflineTakeoverCore.IsOfflineOrPending(t.NetId) && !events[i].IsFinished).Any();
            if (!allOnlineFinished) return;

            if (hasPendingGhost)
            {
                OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, nameof(EventUnblockPatch), retryDelayMs, () =>
                {
                    if (!ReferenceEquals(RunManager.Instance.EventSynchronizer, __instance)) return;
                    Postfix(__instance);
                });
                return;
            }

            for (var i = 0; i < playerCollection.Players.Count; i++)
            {
                if (i >= events.Count) break;
                var ghostPlayer = playerCollection.Players[i];
                if (!OfflineTakeoverCore.IsGhost(ghostPlayer.NetId) || events[i].IsFinished) continue;

                var safeGuard = 0;
                while (events.Count > i && !events[i].IsFinished && events[i].CurrentOptions.Count > 0 && safeGuard < 5)
                {
                    ChooseMethod?.Invoke(__instance, [ghostPlayer, 0]);
                    OfflineTakeoverCore.BroadcastGhostMessageToClients(new OptionIndexChosenMessage
                    {
                        type = OptionIndexType.Event,
                        optionIndex = 0,
                        location = runState.RunLocation
                    }, ghostPlayer.NetId);
                    safeGuard++;
                }
            }
        }
    }
}

// ==========================================
// 泛用交互托管
// ==========================================
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))]
public static class AutoPassRemoteChoiceForGhostsPatch
{
    static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

    public static void Prefix(PlayerChoiceSynchronizer __instance, Player player, uint choiceId)
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;
        if (RunManager.Instance.NetService.Type != NetGameType.Host) return;
        if (!OfflineTakeoverCore.IsGhost(player.NetId))
        {
            if (OfflineTakeoverCore.IsPendingGhost(player.NetId, out var retryDelayMs))
            {
                OfflineTakeoverCore.ScheduleTakeoverRetry(__instance, $"{nameof(AutoPassRemoteChoiceForGhostsPatch)}:{player.NetId}:{choiceId}", retryDelayMs, () =>
                {
                    Prefix(__instance, player, choiceId);
                });
            }

            return;
        }

        var defaultNetResult = PlayerChoiceResult.FromIndex(0).ToNetData();
        __instance.ReceiveReplayChoice(player, choiceId, defaultNetResult);
    }
}
