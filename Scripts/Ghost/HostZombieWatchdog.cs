using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Ghost;

/// <summary>
/// v0.6.3 — 僵尸客机看门狗（主机侧）。
///
/// 问题背景：
///   客机断网后，若 Steam 在 ~15s 内把 P2P transport 层"静默重连"成功（未走 ClientRejoin 握手），
///   则客机逻辑层以为"已连接"、主机侧 Steam 也仍把该 peer 当作在线。
///   于是 OnDisconnectedFromClientAsHost 永不触发 → 主机不托管（"回合不推进"），
///   客机 LocalPlayerDisconnected 也永不触发 → 不重连。两端都以为连着，但游戏状态早已失步。
///   这与正常断线不同：正常断线 Steam 会在 ~15s 判死并触发上述事件，现有托管逻辑即可工作。
///
/// 关键点：sts2 自带心跳（HeartbeatRequest/Response），但心跳走的是同一条 transport 层，
///   transport 活着心跳就照常流动，因此心跳无法区分"僵尸"与"健康但思考中"的玩家。
///   本看门狗改用"战斗回合贡献"作为活跃判据：客机每"结束回合 / 准备敌人阶段"就刷新时间戳；
///   若某已连接、非托管客机是"回合推进的唯一阻塞者"且自上次贡献起沉默超过 ZombieSilenceMs，
///   则判定为僵尸，调用 OfflineTakeoverCore.MarkPeerDisconnected —— 完全复用现有
///   "8s 托管倒计时 + 重连取消"链路（与 OnDisconnectedFromClientAsHost 行为一致）。
///
/// 这样：僵尸最多在 ZombieSilenceMs 后被托管推进，不再永久卡死；且不影响正常思考中的玩家
///   （只要他不是"其他人已就绪后仍然卡住 18s"的唯一阻塞者）。
/// </summary>
internal static class HostZombieWatchdog
{
    /// <summary>沉默阈值（毫秒）：客机作为回合唯一阻塞者且超过此时长无回合贡献，即判定为僵尸。默认 18s（&gt; Steam 15s 超时）。可在 ModConfig 中调整。</summary>
    public static ulong ZombieSilenceMs = 18000;

    /// <summary>peerId → 该客机最后一次"回合贡献"（结束回合 / 准备敌人阶段）的本地毫秒时间。</summary>
    private static readonly Dictionary<ulong, ulong> LastContribution = new();

    /// <summary>
    /// v0.7.1 — peerId → 该 peer 的 transport 最近一次连上主机的本地毫秒时间。
    /// 用于"重连握手宽限窗口"：transport 刚连上时无法区分
    ///   (a) Steam 静默重连的僵尸（永远不会发 ClientRejoinRequest）
    ///   (b) 客机 ReconnectRunner / MenuRejoinFlow 发起的真实重连（马上就会发 ClientRejoinRequest）
    /// 旧实现在 OnPeerConnected 里立刻 DisconnectClient(now:true)，把 (b) 也一并踢掉，
    /// 客机侧表现为 1016 "Application closed connection" + "未收到游戏信息握手" → "连接超时"。
    /// 现在改为：连上后先记录时间戳、放行；只有过了宽限期仍未完成 rejoin 握手，才判定为僵尸。
    /// </summary>
    private static readonly Dictionary<ulong, ulong> PeerConnectedAtMs = new();

    /// <summary>
    /// v0.7.1 — peerId → 强断冷却截止时间（本地毫秒）。一次强断后在该时间之前不再重复强断，
    /// 给客机留出完整的"断开 → 重连 → 握手"周期，避免 OnPeerConnected/OnPeerDisconnected 互相
    /// 触发形成强断风暴。
    /// </summary>
    private static readonly Dictionary<ulong, ulong> ForceDisconnectCooldownUntilMs = new();

    /// <summary>
    /// v0.7.1 — 已观察到 ClientRejoinRequestMessage 的 peer（由 OfflinePlayerTakeoverPatches 上报）。
    /// 一旦收到重连握手请求，就证明这是"真实重连"而非僵尸，此后绝不强断。
    /// </summary>
    private static readonly HashSet<ulong> RejoinHandshakeSeen = new();

    /// <summary>transport 连上后的重连握手宽限期（毫秒）。期间绝不强断，让客机把 rejoin 握手走完。</summary>
    public static ulong RejoinGraceMs = 30000;

    /// <summary>两次强断同一 peer 之间的最小间隔（毫秒）。</summary>
    public static ulong ForceDisconnectCooldownMs = 30000;

    private const ulong CheckIntervalMs = 2000;
    private static ulong _lastCheckMs;
    private static bool _wasInCombat;

    /// <summary>
    /// v0.7.1 — 由主机侧 rejoin 消息处理链路上报：该 peer 已发来 ClientRejoinRequestMessage。
    /// 证明它是走正规重连握手的客机，不是 Steam 静默重连的僵尸 → 永久豁免强断。
    /// </summary>
    public static void NoteRejoinHandshake(ulong peerId)
    {
        if (peerId == 0) return;
        if (RejoinHandshakeSeen.Add(peerId))
            Diag.Log($"[AutoReconnect] 僵尸看门狗：收到客机 {peerId} 的重连握手请求，豁免强断。");
        // 握手已开始，顺带把宽限窗口刷新到当下，避免慢握手在窗口边缘被误伤
        PeerConnectedAtMs[peerId] = Time.GetTicksMsec();
    }

    /// <summary>主机每帧 Update 驱动看门狗（NetHostGameService.Update 仅在主机调用）。</summary>
    [HarmonyPatch(typeof(NetHostGameService), "Update")]
    internal static class HostZombieWatchdogUpdatePatch
    {
        static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

        [HarmonyPostfix]
        private static void Postfix() => Tick();
    }

    /// <summary>
    /// v0.7.1（原 v0.6.5 强断逻辑的修正版）—— 客机 transport 连上主机时**只记录时间戳，不再强断**。
    ///
    /// 旧行为（bug）：Postfix 直接 TryForceDisconnectZombie(peerId)，而正常重连的客机在
    /// transport 刚连上、还没来得及发 ClientRejoinRequestMessage 时也仍处于 Ghost 原始态，
    /// 于是被主机自己的看门狗以 DisconnectClient(NetError.Timeout, now:true) 立即踢掉。
    /// 客机侧看到的就是 1016 "Application closed connection"、"连接在主机发送游戏信息前失败"，
    /// 最终被 RitsuLib 统一报成"连接超时" —— 这就是战斗中/非战斗都重连不上的真凶。
    ///
    /// 新行为：连上先放行 + 打时间戳；真正的僵尸（永远不发 rejoin 握手）会在 Tick 里
    /// 超过 RejoinGraceMs 后才被强断，真实重连则在宽限期内完成握手并被永久豁免。
    /// </summary>
    [HarmonyPatch(typeof(NetHostGameService), "OnPeerConnected")]
    internal static class HostZombiePeerConnectedPatch
    {
        static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

        [HarmonyPostfix]
        private static void Postfix(ulong peerId)
        {
            if (peerId == 0) return;
            PeerConnectedAtMs[peerId] = Time.GetTicksMsec();
            Diag.Log($"[AutoReconnect] 僵尸看门狗：PeerConnectedAtMs[{peerId}] = {PeerConnectedAtMs[peerId]}（记录 transport 连上时间）");
            if (OfflineTakeoverCore.IsMarkedGhostRaw(peerId))
                Diag.Log($"[AutoReconnect] 僵尸看门狗：托管中客机 {peerId} 的 transport 已连上，进入 {RejoinGraceMs}ms 重连握手宽限期（不强断）。");
        }
    }

    /// <summary>
    /// v0.7.1 — 客机断开时清理其连接时间戳与握手标记，让下一轮连接重新计时。
    /// 强断冷却（ForceDisconnectCooldownUntilMs）**不清理**，否则会被 disconnect→connect 循环重置。
    /// </summary>
    [HarmonyPatch(typeof(NetHostGameService), "OnPeerDisconnected")]
    internal static class HostZombiePeerDisconnectedPatch
    {
        static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

        [HarmonyPostfix]
        private static void Postfix(ulong peerId)
        {
            if (peerId == 0) return;
            PeerConnectedAtMs.Remove(peerId);
            RejoinHandshakeSeen.Remove(peerId);
        }
    }

    /// <summary>客机"结束回合"即视为一次明确贡献，刷新其活跃时间戳。</summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToEndTurn))]
    internal static class HostZombieContributionPatch
    {
        static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

        [HarmonyPostfix]
        private static void Postfix(CombatManager __instance, Player player)
        {
            if (player == null || !player.Creature.IsAlive) return;
            if (RunManager.Instance is not { NetService.Type: NetGameType.Host }) return;

            ulong hostNetId;
            try { hostNetId = RunManager.Instance.NetService.NetId; }
            catch { hostNetId = 0; }
            if (player.NetId == hostNetId) return;
            if (OfflineTakeoverCore.IsGhost(player.NetId) || OfflineTakeoverCore.IsOfflineOrPending(player.NetId)) return;

            LastContribution[player.NetId] = Time.GetTicksMsec();
        }
    }

    /// <summary>客机"准备进入敌人回合"也视为贡献（与结束回合并列的活跃信号）。</summary>
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToBeginEnemyTurn))]
    internal static class HostZombieContribution2Patch
    {
        static bool Prepare() => OfflineTakeoverCore.IsTakeoverConfigEnabled();

        [HarmonyPostfix]
        private static void Postfix(CombatManager __instance, Player player)
        {
            if (player == null || !player.Creature.IsAlive) return;
            if (RunManager.Instance is not { NetService.Type: NetGameType.Host }) return;

            ulong hostNetId;
            try { hostNetId = RunManager.Instance.NetService.NetId; }
            catch { hostNetId = 0; }
            if (player.NetId == hostNetId) return;
            if (OfflineTakeoverCore.IsGhost(player.NetId) || OfflineTakeoverCore.IsOfflineOrPending(player.NetId)) return;

            LastContribution[player.NetId] = Time.GetTicksMsec();
        }
    }

    private static void Tick()
    {
        if (!OfflineTakeoverCore.IsTakeoverActive) return;

        // v0.9.0 —— 回退期间一律停手。
        // 主机原地重载会先把在线客机踢下线、再重建 run，这段时间客机正在陆续重连回来；
        // 看门狗此刻若照常判定"僵尸"，会把刚连上、还没来得及握手的客机再踢一次，
        // 形成"踢→连→再踢"的死循环（v0.7.0 的 1016 就是同类误伤）。
        if (Checkpoint.CheckpointRollback.IsRollingBack) return;

        var rm = RunManager.Instance;
        if (rm is not { NetService: NetHostGameService host }) return;

        ulong now = Time.GetTicksMsec();
        if (now - _lastCheckMs < CheckIntervalMs) return;
        _lastCheckMs = now;

        // v0.7.1 — 僵尸强断检查放在战斗专属逻辑之前：任何已被托管(Ghost)但 transport 仍连着、
        // 且过了宽限期仍未发起重连握手的客机，无论是否处于战斗，都强制断开以触发其真实重连
        // （覆盖"非战斗阶段也卡死"的僵尸）。注意必须在 CheckIntervalMs 节流之后执行，
        // 否则每帧都跑一遍反射/字典查询，且旧版会在 OnPeerConnected 的同一帧误伤正常重连。
        foreach (var peer in host.ConnectedPeers)
            TryForceDisconnectZombie(peer.peerId);

        var cm = CombatManager.Instance;
        if (cm == null) return;
        bool inCombat = cm.IsInProgress;
        // 新战斗开始：重置沉默基线，避免沿用上一场战斗的贡献时间
        if (inCombat && !_wasInCombat) LastContribution.Clear();
        _wasInCombat = inCombat;
        if (!inCombat) return;

        var state = cm.DebugOnlyGetState();
        if (state == null || state.CurrentSide != CombatSide.Player) return;
        if (state!.Players.All(p => OfflineTakeoverCore.IsGhost(p.NetId))) return;

        ulong hostNetId;
        try { hostNetId = rm.NetService.NetId; }
        catch { hostNetId = 0; }

        // 是否已有其他在线玩家就绪：说明战斗确实在等剩余未就绪者（真正阻塞），
        // 而非"回合刚开始、大家都还没行动"（此时不应判定任何人为僵尸）。
        bool anyOtherReady = false;
        foreach (var p in state.Players)
        {
            if (p.NetId == hostNetId) continue;
            if (OfflineTakeoverCore.IsGhost(p.NetId) || OfflineTakeoverCore.IsOfflineOrPending(p.NetId)) continue;
            if (!p.Creature.IsAlive) continue;
            if (cm.IsPlayerReadyToEndTurn(p)) { anyOtherReady = true; break; }
        }
        if (!anyOtherReady) return;

        foreach (var p in state.Players)
        {
            if (p.NetId == hostNetId) continue;
            if (OfflineTakeoverCore.IsGhost(p.NetId) || OfflineTakeoverCore.IsOfflineOrPending(p.NetId)) continue;
            if (!p.Creature.IsAlive) continue;
            if (cm.IsPlayerReadyToEndTurn(p)) continue; // 已就绪，不是阻塞者

            // 新出现的在线玩家先给一个新鲜窗口，避免刚加入即被误判
            if (!LastContribution.TryGetValue(p.NetId, out var last))
            {
                LastContribution[p.NetId] = now;
                continue;
            }

            ulong silent = now - last;
            if (silent > ZombieSilenceMs)
            {
                Diag.Log($"[AutoReconnect] 僵尸检测：客机 {p.NetId} 是回合唯一阻塞者且已沉默 {silent}ms（> {ZombieSilenceMs}ms），判定为僵尸并启动托管。");
                OfflineTakeoverCore.MarkPeerDisconnected(p.NetId, NetError.Timeout);
                LastContribution.Remove(p.NetId);
            }
        }
    }

    /// <summary>
    /// v0.7.1 — 尝试强制断开"已托管(Ghost)、transport 连着、但迟迟不走 rejoin 握手"的僵尸客机。
    /// 只在 Tick 中周期调用（**不再**在 OnPeerConnected 里调用）。
    ///
    /// 强断的全部前置条件（任一不满足即放行，绝不误伤正常重连）：
    ///   ① peer 仍处原始 Ghost 态（绕过防抖，避免把连着的僵尸误判为 Online）；
    ///   ② 该 peer 从未发来过 ClientRejoinRequestMessage（发过 = 真实重连，永久豁免）；
    ///   ③ transport 连上已超过 RejoinGraceMs 宽限期（给握手留足时间）；
    ///   ④ 不在强断冷却期内（避免 disconnect/connect 循环形成强断风暴）；
    ///   ⑤ 战斗尚未托管推进过该玩家（否则重连本来就会被拒，强断只会白白折腾）。
    /// </summary>
    private static void TryForceDisconnectZombie(ulong peerId)
    {
        try
        {
            if (peerId == 0) return;
            if (!OfflineTakeoverCore.IsTakeoverActive) return;
            if (RunManager.Instance is not { NetService: NetHostGameService host }) return;
            if (host.NetId == peerId) return;

            // ① 已恢复（不再处于原始 Ghost 态）→ 清理所有跟踪状态
            if (!OfflineTakeoverCore.IsMarkedGhostRaw(peerId))
            {
                Diag.Log($"[AutoReconnect] 僵尸检测跳过：peer {peerId} 已不再处于 Ghost 态，清理跟踪并放行。");
                PeerConnectedAtMs.Remove(peerId);
                RejoinHandshakeSeen.Remove(peerId);
                ForceDisconnectCooldownUntilMs.Remove(peerId);
                return;
            }

            // ② 已观察到重连握手请求 → 这是真实重连，绝不强断
            if (RejoinHandshakeSeen.Contains(peerId))
            {
                Diag.Log($"[AutoReconnect] 僵尸检测跳过：peer {peerId} 已发起重连握手，豁免强断。");
                return;
            }

            ulong now = Time.GetTicksMsec();

            // ③ 宽限期内不强断；首次见到该 peer（如 mod 中途加载）也先给一个完整宽限窗口
            if (!PeerConnectedAtMs.TryGetValue(peerId, out var connectedAt))
            {
                PeerConnectedAtMs[peerId] = now;
                Diag.Log($"[AutoReconnect] 僵尸检测：首次记录 peer {peerId} 的 connectedAt，设为 {now}，等待宽限期后再评估。");
                return;
            }
            if (now - connectedAt < RejoinGraceMs)
            {
                var elapsed = now - connectedAt;
                Diag.Log($"[AutoReconnect] 僵尸检测：peer {peerId} 在宽限期内（{elapsed}ms < {RejoinGraceMs}ms），暂不强断。");
                return;
            }

            // ④ 强断冷却
            if (ForceDisconnectCooldownUntilMs.TryGetValue(peerId, out var cooldownUntil) && now < cooldownUntil)
            {
                Diag.Log($"[AutoReconnect] 僵尸检测：peer {peerId} 在强断冷却期内，冷却截止于 {cooldownUntil}（本地 {now}），跳过强断。");
                return;
            }

            // ⑤ 若战斗已托管推进过该玩家，重连会被拒（等本场战斗结束）——此时不要强断，
            //    让主机继续托管（主机结束回合 + 客机 ghost 结束回合的兜底行为）。
            if (OfflineTakeoverCore.ShouldRejectRunningRejoin(peerId, out var rejectReason, out var extra))
            {
                Diag.Log($"[AutoReconnect] 僵尸检测：peer {peerId} 的重连会被拒绝（ShouldRejectRunningRejoin），原因：{rejectReason} {extra}");
                return;
            }

            ForceDisconnectCooldownUntilMs[peerId] = now + ForceDisconnectCooldownMs;
            ulong silentMs = now - connectedAt;
            Diag.Log($"[AutoReconnect] 僵尸检测：已托管客机 {peerId} transport 连着 {silentMs}ms 仍未发起重连握手，判定为静默僵尸，强制断开以触发其重连。");
            ReconnectDiagnostics.ReportHostEvent(
                HostReconnectEvent.ZombieForceDisconnected,
                peerId,
                $"transport 已连上 {silentMs / 1000}s 但始终没有发起重连握手（Steam 静默重连造成的僵尸连接），已强制断开，客机将自动重连。");
            host.DisconnectClient(peerId, NetError.Timeout, now: true);
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] TryForceDisconnectZombie EX: {ex.Message}");
        }
    }
}
