using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.2.0 — 重连主控制器（主线程 Godot Node）。
///
/// 每次重连流程（仿 Game Lobby 的 ManagedJoinFlow rejoin 分支）：
///   1. 新建 NetClientGameService（关键修复：绝不复用已死实例）
///   2. SteamClientConnectionInitializer.Connect() 建立 Steam P2P + ENet 通道
///   3. 等待 InitialGameInfoMessage，读取 sessionState
///   4. sessionState == Running 时发送 ClientRejoinRequestMessage
///   5. 等待 ClientRejoinResponseMessage
///   6. 把新 NetService 反射赋回 RunManager.NetService
///
/// NetService.Update() 由本节点的 _Process 每帧驱动（与 Game Lobby 的 update loop 一致）。
/// 消息等待使用 Godot 帧轮询（await ToSignal(ProcessFrame)），确保全程在主线程，
/// 满足 Godot/Steamworks 的线程安全要求。
/// </summary>
public partial class ReconnectRunner : Node
{
    private const int MaxRetries = 3;
    private const float RetryDelaySeconds = 5.0f;
    private const float HandshakeTimeoutSeconds = 20.0f;

    private int _attempt;
    private bool _isRunning;
    private ReconnectOverlay? _overlay;
    private NetClientGameService? _netService;
    private CancellationTokenSource? _cts;

    /// <summary>v0.7.1 — 最近一次尝试的失败详情，用于失败弹窗展示「卡在哪一步 + 为什么」。</summary>
    private ReconnectFailure? _lastFailure;

    /// <summary>v0.7.1 — 最近一次使用的连接方式描述（FromLobby / FromPlayer），用于弹窗。</summary>
    private string _connLabel = "未知";

    /// <summary>
    /// v0.7.1 — <see cref="AwaitWithTimeoutAndDisconnect{T}"/> 的失败原因传出通道。
    /// C# 不允许 async 方法带 out 参数，因此用字段承载。
    /// </summary>
    private string _lastWaitFailReason = "未知原因";

    /// <summary>主机 Steam ID（断线瞬间由触发方捕获并写入），用于 FromPlayer 重建 Steam lobby 连接。0 表示未知。</summary>
    public ulong HostSteamId { get; set; }

    private TaskCompletionSource<InitialGameInfoMessage>? _connectCompletion;
    private TaskCompletionSource<ClientRejoinResponseMessage>? _rejoinCompletion;
    private TaskCompletionSource<NetErrorInfo>? _disconnectCompletion;

    public override void _Ready()
    {
        _attempt = 0;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        ShowOverlay();
        Diag.Log("ReconnectRunner._Ready: starting async reconnect (v0.6.5)");
        _ = RunReconnectAsync();
    }

    public override void _Process(double delta)
    {
        if (_netService != null)
        {
            try
            {
                _netService.Update();
            }
            catch (Exception ex)
            {
                Diag.Log($"ReconnectRunner._Process: NetService.Update threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ShowOverlay()
    {
        if (_overlay == null || !GodotObject.IsInstanceValid(_overlay))
            _overlay = ReconnectOverlay.Create(this);
    }

    private async Task RunReconnectAsync()
    {
        while (_isRunning && _attempt < MaxRetries)
        {
            _attempt++;
            if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
                _overlay.SetAttempt(_attempt, MaxRetries);
            Diag.Log($"ReconnectRunner: attempt {_attempt}/{MaxRetries}");

            var failure = await PerformReconnectAsync(_cts!.Token);
            _lastFailure = failure;
            if (failure == null)
            {
                OnReconnectSuccess();
                return;
            }

            if (_isRunning && _attempt < MaxRetries && !_cts.IsCancellationRequested)
            {
                Diag.Log($"ReconnectRunner: attempt {_attempt} failed ({failure}), retry in {RetryDelaySeconds}s");
                var timer = GetTree().CreateTimer(RetryDelaySeconds);
                await ToSignal(timer, Godot.Timer.SignalName.Timeout);
            }
            else
            {
                break;
            }
        }

        if (_isRunning)
            OnReconnectFailed();
    }

    /// <summary>
    /// 执行一次完整重连。返回 <c>null</c> 表示成功；返回 <see cref="ReconnectFailure"/> 时
    /// 携带「卡在哪一步 + 为什么」，供失败弹窗展示（v0.7.1 之前只返回 bool，原因全丢了）。
    /// </summary>
    private async Task<ReconnectFailure?> PerformReconnectAsync(CancellationToken token)
    {
        var stage = ReconnectStage.ResolvingTarget;
        try
        {
            // 1. 新建 NetService（关键修复：v0.1.8 复用已死实例是错的）
            _netService = new NetClientGameService();
            _connectCompletion = new TaskCompletionSource<InitialGameInfoMessage>();
            _rejoinCompletion = new TaskCompletionSource<ClientRejoinResponseMessage>();
            _disconnectCompletion = new TaskCompletionSource<NetErrorInfo>();

            _netService.RegisterMessageHandler<InitialGameInfoMessage>(HandleInitialGameInfo);
            _netService.RegisterMessageHandler<ClientRejoinResponseMessage>(HandleRejoinResponse);
            _netService.Disconnected += HandleDisconnected;

            Diag.Log("PerformReconnect: NetService created, resolving connection target...");

            // 2. 解析连接方式。统一走 ReconnectService.CreateInitializer：
            //    优先 FromLobby(捕获到的大厅 ID)，回退 FromPlayer(主机 Steam ID)。
            //    （长断线后 GetFriendGamePlayed 会失效，FromPlayer 直接 InvalidJoin。）
            var initializer = ReconnectService.CreateInitializer(HostSteamId, out _connLabel, out var resolveFail);
            if (initializer == null)
            {
                CleanupNetService();
                return new ReconnectFailure(ReconnectStage.ResolvingTarget, resolveFail);
            }

            stage = ReconnectStage.Connecting;
            Diag.Log($"PerformReconnect: connecting via {_connLabel} ...");
            NetErrorInfo? connectError = await initializer.Connect(_netService, token);
            if (connectError.HasValue)
            {
                var why = ReconnectDiagnostics.DescribeNetErrorInfo(connectError.Value);
                Diag.Log($"PerformReconnect: connect FAILED: {why}");
                CleanupNetService();
                return new ReconnectFailure(stage, $"{why}｜连接方式：{_connLabel}");
            }
            Diag.Log("PerformReconnect: connected, awaiting InitialGameInfoMessage...");

            // 3. 等 InitialGameInfoMessage
            stage = ReconnectStage.AwaitingGameInfo;
            var initial = await AwaitWithTimeoutAndDisconnect(
                _connectCompletion.Task, "InitialGameInfoMessage");
            if (initial == null)
            {
                CleanupNetService();
                return new ReconnectFailure(stage, _lastWaitFailReason);
            }

            Diag.Log($"PerformReconnect: sessionState={initial.Value.sessionState}");

            // 4. 仅 Running 状态可重连
            stage = ReconnectStage.CheckingSession;
            if (initial.Value.sessionState != RunSessionState.Running)
            {
                Diag.Log($"PerformReconnect: sessionState={initial.Value.sessionState} != Running, cannot rejoin");
                CleanupNetService();
                return new ReconnectFailure(stage,
                    $"主机当前会话状态是 {initial.Value.sessionState}，不是「对局进行中(Running)」");
            }

            // 5. 发 ClientRejoinRequestMessage，等 ClientRejoinResponseMessage
            //    必须用 PeerVersionInfo.LocalDefault() 填充 versionInfo，否则 version=null
            //    会在序列化 WriteString(null) 时抛 ArgumentNullException（v0.6.1 崩溃真凶）。
            stage = ReconnectStage.Handshaking;
            Diag.Log("PerformReconnect: sending ClientRejoinRequestMessage...");
            _netService.SendMessage(new ClientRejoinRequestMessage { versionInfo = PeerVersionInfo.LocalDefault() });

            var rejoin = await AwaitWithTimeoutAndDisconnect(
                _rejoinCompletion.Task, "ClientRejoinResponseMessage");
            if (rejoin == null)
            {
                CleanupNetService();
                return new ReconnectFailure(stage, _lastWaitFailReason);
            }
            Diag.Log("PerformReconnect: rejoin response received");

            // 6. 赋回 RunManager.NetService
            stage = ReconnectStage.RestoringRun;
            if (!ReconnectService.AssignNetServiceToRunManager(_netService))
            {
                Diag.Log("PerformReconnect: FAILED to assign NetService to RunManager");
                CleanupNetService();
                return new ReconnectFailure(stage, "无法把新连接接回 RunManager（反射未找到可写的 NetService 成员）");
            }
            Diag.Log("PerformReconnect: NetService assigned to RunManager, reconnect SUCCESS");

            // 保留 _netService（不 cleanup），让 _Process 继续驱动它
            return null;
        }
        catch (OperationCanceledException)
        {
            Diag.Log("PerformReconnect: cancelled");
            CleanupNetService();
            return new ReconnectFailure(stage, "重连被取消");
        }
        catch (Exception ex)
        {
            Diag.Log($"PerformReconnect: EXCEPTION - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            CleanupNetService();
            return new ReconnectFailure(stage, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 主线程帧轮询等待：每帧 await ProcessFrame，检查 completion / 断线 / 超时。
    /// 避免使用 Task.Delay/Task.Run，确保 NetService 操作始终在主线程。
    ///
    /// 返回 null 时，失败的具体原因写在 <see cref="_lastWaitFailReason"/>
    /// （async 方法不能带 out 参数，故用字段传出），
    /// 让上层弹窗能区分「主机把我踢了(1016)」和「主机没理我(超时)」。
    /// </summary>
    private async Task<T?> AwaitWithTimeoutAndDisconnect<T>(Task<T> task, string label) where T : struct
    {
        _lastWaitFailReason = "未知原因";
        ulong startMs = Time.GetTicksMsec();
        ulong timeoutMs = (ulong)(HandshakeTimeoutSeconds * 1000);
        Diag.Log($"Await({label}): 开始等待，超时设置 {HandshakeTimeoutSeconds:F0}s（{timeoutMs}ms）");
        while (_isRunning && !_cts!.IsCancellationRequested)
        {
            if (task.IsCompleted)
                return await task;
            if (_disconnectCompletion!.Task.IsCompleted)
            {
                var info = await _disconnectCompletion.Task;
                var why = ReconnectDiagnostics.DescribeNetErrorInfo(info);
                Diag.Log($"Await({label}): disconnected during wait — {why}");
                _lastWaitFailReason = $"等待「{label}」期间被主机断开：{why}";
                return null;
            }
            if (Time.GetTicksMsec() - startMs > timeoutMs)
            {
                Diag.Log($"Await({label}): timeout after {HandshakeTimeoutSeconds}s");
                _lastWaitFailReason = $"等待「{label}」超过 {HandshakeTimeoutSeconds:F0} 秒仍无响应";
                return null;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _lastWaitFailReason = "重连流程已被取消";
        return null;
    }

    private void HandleInitialGameInfo(InitialGameInfoMessage message, ulong _)
    {
        Diag.Log("HandleInitialGameInfo: received");
        _connectCompletion?.TrySetResult(message);
    }

    private void HandleRejoinResponse(ClientRejoinResponseMessage message, ulong _)
    {
        Diag.Log("HandleRejoinResponse: received");
        _rejoinCompletion?.TrySetResult(message);
    }

    private void HandleDisconnected(NetErrorInfo info)
    {
        try
        {
            Diag.Log($"HandleDisconnected: reason={info.GetReason()}");
        }
        catch
        {
            Diag.Log("HandleDisconnected: (reason unavailable)");
        }
        _disconnectCompletion?.TrySetResult(info);
    }

    private void CleanupNetService()
    {
        try
        {
            if (_netService != null)
            {
                _netService.Disconnected -= HandleDisconnected;
                _netService = null;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"CleanupNetService: {ex.Message}");
        }
    }

    private void OnReconnectSuccess()
    {
        Diag.Log("OnReconnectSuccess: cleaning up overlay and runner");
        _isRunning = false;
        ReconnectService.OnReconnectSucceeded();

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.QueueFree();

        // v0.7.1 —— 成功也要给玩家一个明确回执（此前成功是完全静默的，
        // 玩家分不清「真的连回来了」还是「界面卡住了」）。
        ReconnectDiagnostics.ReportClientSuccess(_connLabel, _attempt, MaxRetries,
            "对局已恢复同步，可以继续游戏。");

        // _netService 保留，让 _Process 继续驱动新连接
        QueueFree();
    }

    private void OnReconnectFailed()
    {
        var failure = _lastFailure ?? new ReconnectFailure(ReconnectStage.Idle, "未知原因");
        Diag.Log($"OnReconnectFailed: 重连放弃 — {failure}");
        _isRunning = false;
        ReconnectService.OnReconnectSucceeded();
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.ShowManualButton();

        // v0.7.1 —— 不再统一弹「等本场战斗结束后再重连」（那只是众多失败原因里的一种，
        // 直接对所有失败都这么说会误导玩家）。改为展示真实的阶段 + 原因，并保留「重试」按钮。
        ReconnectDiagnostics.ReportClientFailure(
            failure.Stage,
            failure.Reason,
            _attempt,
            MaxRetries,
            withRetryButton: true,
            hintOverride: $"连接方式：{_connLabel}。\n{ReconnectDiagnostics.StageHint(failure.Stage)}");
    }

    public void OnManualRetry()
    {
        Diag.Log("OnManualRetry: resetting and retrying");
        _attempt = 0;
        _isRunning = true;
        HostInfoTracker.IsReconnecting = true;

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.HideManualButton();

        _ = RunReconnectAsync();
    }

    public void OnCancel()
    {
        Diag.Log("OnCancel: user cancelled reconnection");
        _cts?.Cancel();
        _isRunning = false;
        HostInfoTracker.IsReconnecting = false;
        CleanupNetService();

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.QueueFree();

        QueueFree();
        HostInfoTracker.Reset();

        var rm = RunManager.Instance;
        var method = rm?.GetType().GetMethod("ReturnToMainMenuWithError",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method?.Invoke(rm, new object?[] { null });
    }
}
