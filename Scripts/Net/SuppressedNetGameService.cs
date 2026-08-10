using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;

namespace AutoReconnect.Scripts.Net;

/// <summary>
/// v0.9.0 —— 「断连抑制」装饰器（参考 QuickSL 的 <c>DisconnectSuppressingNetGameService</c>）。
///
/// ## 为什么需要它
///
/// <c>RunManager.CleanUp(bool graceful)</c>（BaseGame RunManager.cs:1569）在收尾时**无条件**执行：
/// <code>
/// NetService.Disconnect(NetError.Quit, !graceful);   // line 1606
/// </code>
/// 也就是说：只要清理当前 run，就一定会把 Steam 大厅 / 连接一起拆掉。
///
/// 这正是 v0.8.x「主机回退 = 回主菜单 + 重新 <c>StartSteamHost</c>」架构的病根：
/// 大厅 ID 变了，掉线客机拿着旧大厅 ID 怎么连都连不回来，最终弹「发现未知错误」。
///
/// ## 用法
///
/// 在调用 <c>CleanUp</c> 前把 <c>RunManager.NetService</c> 临时换成本装饰器，调用后立刻换回原件：
/// <code>
/// var original = rm.NetService;
/// NetServiceSwapper.Set(rm, SuppressedNetGameServiceFactory.Wrap(original));
/// try   { rm.CleanUp(graceful: true); }   // Disconnect 被吞掉，大厅存活
/// finally { NetServiceSwapper.Set(rm, original); }
/// </code>
///
/// ## 为什么要区分 Host / 非 Host 两个实现
///
/// <c>LoadRunLobby</c> 构造函数会对 <c>NetGameType.Host</c> 的服务做 <c>(INetHostGameService)</c> 强转
/// （BaseGame LoadRunLobby.cs:106），<c>RunLobby</c> 更是直接强转到具体类 <c>(NetHostGameService)</c>。
/// 因此：
///   - 主机侧包装必须实现 <see cref="INetHostGameService"/>（否则 CleanUp 里 RunLobby.Dispose 的
///     <c>ClientConnected -=</c> 解绑会 InvalidCastException）；
///   - 包装器**绝不能**被长期留在 RunManager 上（RunLobby 的具体类强转会炸），
///     必须像上面那样 try/finally 立刻换回。
/// </summary>
internal abstract class SuppressedNetGameServiceBase : INetGameService
{
    protected readonly INetGameService Inner;

    /// <summary>被吞掉的 Disconnect 次数，仅用于日志核对（正常一次 CleanUp 恰好 1 次）。</summary>
    public int SuppressedDisconnectCount { get; private set; }

    protected SuppressedNetGameServiceBase(INetGameService inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public ulong NetId => Inner.NetId;
    public bool IsConnected => Inner.IsConnected;
    public bool IsGameLoading => Inner.IsGameLoading;
    public NetGameType Type => Inner.Type;
    public PlatformType Platform => Inner.Platform;

    public event Action<NetErrorInfo>? Disconnected
    {
        add => Inner.Disconnected += value;
        remove => Inner.Disconnected -= value;
    }

    public void SendMessage<T>(T message, ulong playerId) where T : INetMessage
        => Inner.SendMessage(message, playerId);

    public void SendMessage<T>(T message) where T : INetMessage
        => Inner.SendMessage(message);

    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage
        => Inner.RegisterMessageHandler(handler);

    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage
        => Inner.UnregisterMessageHandler(handler);

    public void Update() => Inner.Update();

    /// <summary>★ 核心：吞掉断连请求，让 Steam 大厅 / 连接在 CleanUp 之后继续存活。</summary>
    public void Disconnect(NetError reason, bool now = false)
    {
        SuppressedDisconnectCount++;
        Diag.Log($"[Net] 已拦截 NetService.Disconnect(reason={reason}, now={now})（第 {SuppressedDisconnectCount} 次），连接保活。");
    }

    public ConnectionStats? GetStatsForPeer(ulong peerId) => Inner.GetStatsForPeer(peerId);

    public void SetGameLoading(bool isLoading) => Inner.SetGameLoading(isLoading);

    public void SetBufferMessages(bool bufferMessages) => Inner.SetBufferMessages(bufferMessages);

    public string? GetRawLobbyIdentifier() => Inner.GetRawLobbyIdentifier();
}

/// <summary>非主机（客机 / 单人）用的断连抑制包装。</summary>
internal sealed class SuppressedNetGameService : SuppressedNetGameServiceBase
{
    public SuppressedNetGameService(INetGameService inner) : base(inner) { }
}

/// <summary>
/// 主机用的断连抑制包装。必须实现 <see cref="INetHostGameService"/>，
/// 否则 <c>RunLobby.Dispose()</c> / <c>LoadRunLobby.CleanUp()</c> 里的
/// <c>(INetHostGameService)NetService</c> 强转会抛 InvalidCastException。
/// </summary>
internal sealed class SuppressedNetHostGameService : SuppressedNetGameServiceBase, INetHostGameService
{
    private readonly INetHostGameService _innerHost;

    public SuppressedNetHostGameService(INetHostGameService inner) : base(inner)
    {
        _innerHost = inner;
    }

    public IReadOnlyList<NetClientData> ConnectedPeers => _innerHost.ConnectedPeers;

    public NetHost? NetHost => _innerHost.NetHost;

    public event Action<ulong>? ClientConnected
    {
        add => _innerHost.ClientConnected += value;
        remove => _innerHost.ClientConnected -= value;
    }

    public event Action<ulong, NetErrorInfo>? ClientDisconnected
    {
        add => _innerHost.ClientDisconnected += value;
        remove => _innerHost.ClientDisconnected -= value;
    }

    /// <summary>
    /// 单独踢某个客机是**允许**的——被抑制的只有「拆掉整个会话」的 <c>Disconnect</c>。
    /// 回退流程本来就需要把仍在线的客机踢下去让它们重连回同一大厅。
    /// </summary>
    public void DisconnectClient(ulong peerId, NetError reason, bool now = false)
        => _innerHost.DisconnectClient(peerId, reason, now);

    public void SetPeerReadyForBroadcasting(ulong peerId)
        => _innerHost.SetPeerReadyForBroadcasting(peerId);
}

/// <summary>包装器工厂 + RunManager.NetService 反射读写。</summary>
internal static class NetServiceSwapper
{
    /// <summary>按 inner 的真实类型选择合适的包装（Host → 实现 INetHostGameService 的版本）。</summary>
    public static SuppressedNetGameServiceBase Wrap(INetGameService inner)
        => inner is INetHostGameService host
            ? new SuppressedNetHostGameService(host)
            : new SuppressedNetGameService(inner);

    /// <summary>
    /// 把 <c>RunManager.NetService</c>（<c>public INetGameService NetService { get; private set; }</c>）
    /// 替换为指定实例。返回是否成功。
    /// </summary>
    public static bool Set(MegaCrit.Sts2.Core.Runs.RunManager rm, INetGameService service)
    {
        if (rm == null) return false;
        try
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance;

            var prop = typeof(MegaCrit.Sts2.Core.Runs.RunManager).GetProperty("NetService", flags);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(rm, service);
                return true;
            }

            // 回退：自动属性的编译器后备字段
            var backing = typeof(MegaCrit.Sts2.Core.Runs.RunManager)
                .GetField("<NetService>k__BackingField", flags);
            if (backing != null)
            {
                backing.SetValue(rm, service);
                return true;
            }

            Diag.Log("[Net] 未找到可写的 RunManager.NetService（属性与后备字段都没命中）。");
            return false;
        }
        catch (Exception ex)
        {
            Diag.Log($"[Net] 替换 RunManager.NetService 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 在「断连被抑制」的保护下执行 <c>RunManager.CleanUp</c>，结束后必定还原原始 NetService。
    /// 返回 true 表示确实走了抑制路径（false = 反射失败，已按原样直接 CleanUp，连接会被拆）。
    /// </summary>
    public static bool CleanUpKeepingConnection(MegaCrit.Sts2.Core.Runs.RunManager rm, bool graceful = true)
    {
        var original = rm.NetService;
        if (original == null)
        {
            rm.CleanUp(graceful);
            return false;
        }

        var wrapper = Wrap(original);
        if (!Set(rm, wrapper))
        {
            Diag.Log("[Net] 无法安装断连抑制包装，退化为普通 CleanUp（连接会被断开）。");
            rm.CleanUp(graceful);
            return false;
        }

        try
        {
            rm.CleanUp(graceful);
        }
        finally
        {
            // 必须还原：RunLobby 等处存在到具体类 NetHostGameService 的强转，包装器不能久留。
            Set(rm, original);
        }

        Diag.Log($"[Net] CleanUp 完成且连接保活（拦截 Disconnect {wrapper.SuppressedDisconnectCount} 次）。");
        return true;
    }
}
