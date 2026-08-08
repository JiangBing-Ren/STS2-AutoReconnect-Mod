using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.MenuRejoin;

/// <summary>
/// v0.7.0 —— 客机「从主菜单重连进对局」核心流程。
///
/// ## 为什么需要这个（根因，已由 IL 反编译 + 双端日志实锤）
///
/// 原版 <c>NJoinFriendScreen.JoinGameAsync</c> 在拿到 <c>JoinResult</c> 后：
/// <code>
/// else if (joinResult.sessionState == RunSessionState.Running)
/// {
///     NErrorPopup.Create(new NetErrorInfo(NetError.RunInProgress, selfInitiated: false)); // 弹错误
///     _currentJoinFlow.NetService.Disconnect(NetError.RunInProgress);                     // 客机自己断开
/// }
/// </code>
/// 也就是说 —— **原版压根没实现「从主菜单重连进行中的对局」**。
///
/// 双端日志证实了这条死路：
///   - 客机 godot.log：`Sending ClientRequestRejoinMessage` → `Received ClientRejoinResponseMessage`
///     （握手成功！）→ 紧接着 `Disconnecting from host (now: False reason: 1008)`（1008 = RunInProgress，
///     **客机自己断的**）→ 玩家卡在「加入好友列表」界面。
///   - 主机 godot (7).log：`Received ClientRejoinRequestMessage` 正常处理，`拒绝玩家…运行中重连` 出现 **0 次**
///     （主机全程放行），主机把玩家标记为已连接 → 这就是「主机画面显示客机已进入」的原因。
///
/// ## 修复思路
///
/// 主机回复的 <see cref="ClientRejoinResponseMessage"/> 里带着 **完整的 <c>SerializableRun</c>**，
/// 足以在客机端重建整个对局。我们复刻原版「多人载入存档」的官方流程
/// （<c>NMultiplayerLoadGameScreen.StartRun</c>）：
/// <code>
/// RunState.FromSerializable(run) → RunManager.SetUpSavedMultiplayer(state, lobby) → NGame.LoadRun(...)
/// </code>
///
/// ## 战斗中为什么仍然不能重连
///
/// <c>ClientRejoinResponseMessage.combatState</c>（<see cref="NetFullCombatState"/>）**只有 <c>FromRun</c>，
/// 没有任何 Apply/Restore 方法** —— 它是 checksum 比对用的只读快照，游戏本身不具备「从快照恢复战斗」的能力。
/// 因此主机若正处于战斗中，重建出来的客机 run 会缺失战斗现场，必然与主机状态分歧。
/// 这种情况下我们不重建，改为提示「等本场战斗结束后再重连」（带重试按钮）。
/// </summary>
internal static class MenuRejoinFlow
{
    /// <summary>菜单重连是否正在进行（防重入，也供其它补丁判断当前语境）。</summary>
    public static bool IsRejoining { get; private set; }

    /// <summary>
    /// 最近一次加入尝试是否是「重连进行中的对局」。
    /// 供 <c>RejoinBlockedPopupPatch</c> 判断是否要把原版 RunInProgress 文案换成
    /// 「等本场战斗结束后再重连」——菜单场景下 RunManager.NetService 为 null，
    /// 原有的 isClient 判断会失效。
    /// </summary>
    public static bool LastAttemptWasRejoin { get; set; }

    /// <summary>
    /// 菜单重连（游戏被 Steam 邀请重拉起）场景下的主机 Steam ID。
    /// 在 <see cref="JoinFlowRejoinPatch"/> 识别出这是一次菜单 rejoin 时写入，
    /// 供「重试」按钮在战斗未结束 / 重建失败时重新发起一次菜单重连握手。
    /// 0 表示当前不处于菜单重连语境——<c>ReconnectBlockedPopup</c> 据此区分
    /// 「菜单重连重试」与「对局内重连重试」（后者走 ReconnectRunner，要求 RunManager 有内存中的 run）。
    /// </summary>
    public static ulong PendingRejoinHostSteamId { get; set; }

    /// <summary>
    /// 判断主机当前是否处于战斗中。
    ///
    /// 依据 <c>NetFullCombatState.FromRun</c> 的实现：
    /// <code>
    /// var creatures = runState.Players[0].Creature.CombatState?.Creatures ?? Array.Empty&lt;Creature&gt;();
    /// </code>
    /// 非战斗时 <c>CombatState</c> 为 null → <c>Creatures</c> 为空列表。
    /// 注意 <c>GetRejoinMessage()</c> 是 **无条件** 生成 combatState 的，
    /// 所以不能用 <c>HasValue</c> 判断，必须看 Creatures 是否为空。
    /// </summary>
    public static bool HostIsInCombat(NetFullCombatState? combatState)
    {
        if (combatState == null) return false;
        try
        {
            return combatState.Creatures is { Count: > 0 };
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 读取 combatState.Creatures 失败（保守视为战斗中）：{ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 用主机回传的存档重建对局并进入。复刻 <c>NMultiplayerLoadGameScreen.StartRun()</c>。
    /// 调用前必须确认：本端没有正在进行的 run（<c>DebugOnlyGetState() == null</c>）。
    /// </summary>
    public static async Task EnterRunFromRejoin(INetClientGameService netService, ClientRejoinResponseMessage rejoin)
    {
        if (IsRejoining)
            throw new InvalidOperationException("菜单重连已在进行中。");

        var run = rejoin.serializableRun;
        if (run == null)
            throw new InvalidOperationException("主机回传的 serializableRun 为空。");

        var game = NGame.Instance;
        if (game == null)
            throw new InvalidOperationException("NGame.Instance 为空，无法加载对局。");

        IsRejoining = true;
        NetUpdatePump? pump = null;
        LoadRunLobby? lobby = null;

        try
        {
            // JoinFlow.Begin 的 finally 已经取消了它自己的 NetService.Update 循环，
            // 而 NRun 场景要等 LoadRun 走完才会接手驱动。中间这段（淡出 + 反序列化 +
            // 资源预加载）可能长达数秒，期间若没人调 Update()，Steam 连接会因收不到
            // 心跳而超时断开。这里挂一个临时泵机保活，LoadRun 完成后立即撤掉。
            netService.SetBufferMessages(bufferMessages: true);
            pump = NetUpdatePump.Attach(netService);

            // LoadRunLobby 是 SetUpSavedMultiplayer 的入参载体，提供 NetService /
            // InputSynchronizer / Run / Players。用 SerializableRun 重载构造时 Players
            // 是空的，必须自己按存档里的玩家补齐，否则 InitializeRunLobby 会拿到空名单。
            lobby = new LoadRunLobby(netService, new PassiveRejoinLobbyListener(), run);
            foreach (var p in run.Players)
            {
                lobby.Players.Add(new LoadRunLobbyPlayer
                {
                    id = p.NetId,
                    versionInfo = PeerVersionInfo.LocalDefault(),
                    isReady = true
                });
            }

            Diag.Log($"[MenuRejoin] 开始重建对局：玩家 {run.Players.Count} 人，第 {run.CurrentActIndex + 1} 幕。");

            await game.Transition.FadeOut();

            var runState = RunState.FromSerializable(run);
            await RunManager.Instance.SetUpSavedMultiplayer(runState, lobby);
            await game.LoadRun(runState, run.PreFinishedRoom);

            // disconnectSession: false —— 只注销 lobby 的消息处理器，
            // 连接本身要留给已经接管的 RunManager 继续用。
            lobby.CleanUp(disconnectSession: false);
            lobby = null;

            // v0.7.1 —— 必须把缓冲关掉。上面为了熬过「JoinFlow 收工 → NRun 接手」的真空期
            // 开了 SetBufferMessages(true)，v0.7.0 全流程**没有任何一处把它关回去**：
            // 于是重连进局后主机发来的所有消息都被一直堆在缓冲区里、永不派发，
            // 客机表现为「进去了但完全不同步 / 卡住不动」。
            // 这里在 RunManager 已注册好处理器、lobby 处理器已注销之后关闭缓冲，
            // 让积压的消息一次性派发给正确的接收方。
            try
            {
                netService.SetBufferMessages(bufferMessages: false);
                Diag.Log("[MenuRejoin] 已关闭消息缓冲，积压消息交由 RunManager 处理。");
            }
            catch (Exception ex)
            {
                Diag.Log($"[MenuRejoin] 关闭消息缓冲失败（可能导致进局后不同步）：{ex.Message}");
            }

            pump?.Detach();
            pump = null;

            await game.Transition.FadeIn();

            Diag.Log("[MenuRejoin] 重连成功，已进入对局。");
            PendingRejoinHostSteamId = 0;
            HostInfoTracker.CaptureFromGame();
        }
        catch (Exception)
        {
            // 半途失败会留下一个残缺的 RunManager.State，必须清干净，
            // 否则玩家回到主菜单后再开新局会撞上 "State is already set."。
            // 缓冲开关同样要还原，否则这条连接即便被后续流程复用也收不到任何消息。
            try { netService.SetBufferMessages(bufferMessages: false); } catch { }
            try { lobby?.CleanUp(disconnectSession: true, NetError.InternalError); } catch { }
            try { pump?.Detach(); } catch { }
            try
            {
                if (RunManager.Instance.DebugOnlyGetState() != null)
                {
                    RunManager.Instance.CleanUp(graceful: false);
                    Diag.Log("[MenuRejoin] 已清理半途失败的 RunManager 状态。");
                }
            }
            catch (Exception cleanupEx)
            {
                Diag.Log($"[MenuRejoin] 清理残留状态时出错：{cleanupEx.Message}");
            }
            throw;
        }
        finally
        {
            IsRejoining = false;
        }
    }

    /// <summary>
    /// 菜单重连的「重试」入口：用上一次握手拿到的主机 Steam ID 重新发起一次完整握手，
    /// 并复用 <see cref="JoinFlowRejoinPatch.InterceptAsync"/> 的判定逻辑。
    ///
    /// 与「对局内重连」的 ReconnectRunner 不同——菜单重连时 RunManager 里没有内存中的 run，
    /// 不能靠 AssignNetServiceToRunManager 直接换连接，必须走完整握手 + 重建对局。
    /// 典型触发：主机当时处于战斗中（本次被拦），等战斗结束后点「重试」即可真正进局。
    /// 若战斗仍未结束，<see cref="JoinFlowRejoinPatch.InterceptAsync"/> 会再次拦下并弹出提示，
    /// 形成「点重试 → 仍被拦 → 再等 → 再重试」的良性循环，而非卡死。
    /// </summary>
    public static async Task RetryRejoin()
    {
        if (IsRejoining)
        {
            Diag.Log("[MenuRejoin] 菜单重连正在进行中，忽略本次重试。");
            return;
        }

        // v0.7.1 —— 改走公共工厂 ReconnectService.CreateInitializer。
        // v0.7.0 这里写死了 SteamClientConnectionInitializer.FromPlayer(hostId)，
        // 把 v0.6.1 已经修好的「优先 FromLobby」又丢了回去（回归 bug）：
        // 长断线后 GetFriendGamePlayed 拿不到主机的有效大厅信息 → FromPlayer 直接 InvalidJoin，
        // 玩家看到的就是「点重试没反应 / 连接超时」。
        var hostId = PendingRejoinHostSteamId;
        var initializer = ReconnectService.CreateInitializer(hostId, out var connLabel, out var failReason);
        if (initializer == null)
        {
            Diag.Log($"[MenuRejoin] 无法发起菜单重连重试：{failReason}");
            ReconnectDiagnostics.ReportClientFailure(
                ReconnectStage.ResolvingTarget,
                failReason,
                attempt: 1,
                maxAttempts: 1,
                withRetryButton: false);
            return;
        }

        try
        {
            Diag.Log($"[MenuRejoin] 发起菜单重连重试，连接方式 = {connLabel}");
            var flow = new JoinFlow(new NetClientGameService());
            var inner = flow.Begin(initializer, Engine.GetMainLoop() as SceneTree);
            // InterceptAsync 会：识别 rejoin →（非战斗）重建对局 /（战斗中）再次提示，
            // 并维持 LastAttemptWasRejoin / PendingRejoinHostSteamId 语义。
            await JoinFlowRejoinPatch.InterceptAsync(flow, inner).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 菜单重连重试异常：{ex}");
            ReconnectDiagnostics.ReportClientFailure(
                ReconnectStage.Connecting,
                $"{ex.GetType().Name}: {ex.Message}",
                attempt: 1,
                maxAttempts: 1,
                hintOverride: $"连接方式：{connLabel}。若反复失败，请让房主重新从 Steam 邀请你。");
        }
    }
}

/// <summary>
/// <see cref="ILoadRunLobbyListener"/> 的空实现。
///
/// 重连场景下不存在「大厅界面」，也没有准备/开始流程需要回调：
/// run 是我们自己直接拉起来的，不走 <c>BeginRun</c> 那条路。
/// 这些回调只在 lobby 存活的极短窗口内可能被触发，全部忽略即可。
/// </summary>
internal sealed class PassiveRejoinLobbyListener : ILoadRunLobbyListener
{
    public void PlayerConnected(LoadRunLobbyPlayer player) { }
    public void RemotePlayerDisconnected(ulong playerId) { }
    public Task<bool> ShouldAllowRunToBegin() => Task.FromResult(true);
    public void BeginRun() { }
    public void PlayerReadyChanged(ulong playerId) { }
    public void LocalPlayerDisconnected(NetErrorInfo info) { }
}

/// <summary>
/// 临时网络泵机：在「JoinFlow 已收工、NRun 尚未接手」的真空期里持续调用
/// <c>NetService.Update()</c>，避免连接因长时间无人轮询而超时。
/// </summary>
internal sealed partial class NetUpdatePump : Node
{
    private INetGameService? _service;

    public static NetUpdatePump? Attach(INetGameService service)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var pump = new NetUpdatePump
            {
                _service = service,
                Name = "AutoReconnect_NetPump"
            };
            tree.Root.AddChild(pump);
            return pump;
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 无法挂载网络泵机（继续但连接可能超时）：{ex.Message}");
            return null;
        }
    }

    public void Detach()
    {
        _service = null;
        try { QueueFree(); } catch { }
    }

    public override void _Process(double delta)
    {
        var svc = _service;
        if (svc == null) return;
        try
        {
            if (svc.IsConnected) svc.Update();
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 泵机 Update 异常：{ex.Message}");
            _service = null;
        }
    }
}
