using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.2.0 — 重连核心辅助。
/// 重连由 ReconnectRunner 新建 NetClientGameService 并驱动；本类负责把新建的
/// NetService 反射赋回 RunManager.NetService（QuickSL 已验证反射读写 RunManager.NetService 可行），
/// 以及重连成功后的状态复位。
/// </summary>
internal static class ReconnectService
{
    /// <summary>
    /// v0.7.2 —— 自动重连总开关。
    /// true（默认）：断线后由 ReconnectRunner 在对局内自动重连（FromLobby/FromPlayer 重建 transport）。
    /// false：断线时放行原版 LocalPlayerDisconnected 流程，退回主菜单，
    ///   由 MenuRejoinFlow 走主机存档对局（原生读档：NGame.LoadRun + NMultiplayerLoadGameScreen）重建，
    ///   而非带着可能已分歧的内存态继续。适用于“状态已分歧，想干净重连”的场景。
    /// 由 ModConfig 的 "autoReconnectEnabled" 切换。
    /// </summary>
    public static bool AutoReconnectEnabled = true;

    /// <summary>
    /// 把新建的 NetService 赋回 RunManager，让游戏用新连接继续跑。
    /// 通过反射兼容 NetService 是 property 还是 field（不同版本可能不同）。
    /// </summary>
    public static bool AssignNetServiceToRunManager(NetClientGameService netService)
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null)
            {
                Diag.Log("AssignNetService: RunManager.Instance is null");
                return false;
            }

            var type = rm.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // 优先 NetService 可写属性
            var prop = type.GetProperty("NetService", flags);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(rm, netService);
                Diag.Log("AssignNetService: set via NetService property");
                return true;
            }

            // 其次常见字段名
            foreach (var fname in new[] { "_netService", "netService", "m_NetService", "_netClientGameService", "netClientGameService" })
            {
                var field = type.GetField(fname, flags);
                if (field != null)
                {
                    field.SetValue(rm, netService);
                    Diag.Log($"AssignNetService: set via {fname} field");
                    return true;
                }
            }

            Diag.Log("AssignNetService: no writable NetService property/field found");
            return false;
        }
        catch (Exception ex)
        {
            Diag.Log($"AssignNetService: EXCEPTION - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 重连成功后复位重连状态，允许后续再次触发。
    /// </summary>
    public static void OnReconnectSucceeded()
    {
        HostInfoTracker.IsReconnecting = false;
        HostInfoTracker.DisconnectWasSelfInitiated = false;
    }

    /// <summary>
    /// v0.7.1 —— 统一的「重连连接方式」工厂。**所有重连入口都必须走这里**。
    ///
    /// 背景（v0.7.0 的回归 bug）：
    ///   v0.6.1 已经查明，长断线后 <c>SteamFriends.GetFriendGamePlayed</c> 常常拿不到主机的有效
    ///   STS2 大厅信息，于是 <c>FromPlayer(hostSteamId)</c> → <c>ConnectToLobbyOwnedByFriend</c>
    ///   会直接返回 InvalidJoin —— 表现就是「点了重连什么都没发生 / 提示连接超时」。
    ///   修复办法是优先用连接大厅时捕获到的真实大厅 ID 走 <c>FromLobby</c> 直连。
    ///   但 v0.7.0 新写的 <c>MenuRejoinFlow.RetryRejoin</c> 又退回了裸 <c>FromPlayer</c>，
    ///   把这个修复丢掉了。抽成公共工厂后，任何入口都不会再漏。
    ///
    /// 优先级：FromLobby(捕获到的大厅 ID) → FromPlayer(主机 Steam ID) → 失败。
    /// </summary>
    /// <param name="preferredHostSteamId">调用方已知的主机 Steam ID（0 表示未知，走兜底解析）。</param>
    /// <param name="label">给玩家看的连接方式描述（用于成功/失败弹窗）。</param>
    /// <param name="failReason">返回 null 时的失败原因（中文）。</param>
    public static IClientConnectionInitializer? CreateInitializer(
        ulong preferredHostSteamId,
        out string label,
        out string failReason)
    {
        label = string.Empty;
        failReason = string.Empty;

        // ── 解析主机 Steam ID ──
        // 1) 调用方传入 → 2) HostInfoTracker（Launch/加入时已持久捕获，最可靠）
        // → 3) 当前仍存活的 RunManager.NetService
        ulong hostSteamId = preferredHostSteamId;
        if (hostSteamId == 0)
        {
            try
            {
                var tracked = (ulong)HostInfoTracker.HostSteamId;
                if (tracked != 0) hostSteamId = tracked;
            }
            catch { }
        }
        if (hostSteamId == 0)
        {
            try
            {
                if (RunManager.Instance is { NetService: NetClientGameService csv })
                    hostSteamId = csv.HostNetId;
            }
            catch { }
        }

        // ── 解析真实大厅 ID（由 SteamClientConnectToLobbyCapturePatch 在连接时捕获）──
        ulong lobbyId = 0;
        try
        {
            if (!string.IsNullOrEmpty(HostInfoTracker.LobbyId) &&
                ulong.TryParse(HostInfoTracker.LobbyId, out var parsedLobby))
                lobbyId = parsedLobby;
        }
        catch { }

        if (lobbyId != 0)
        {
            label = $"Steam 大厅直连（lobby {lobbyId}）";
            Diag.Log($"CreateInitializer: 使用 FromLobby({lobbyId})");
            return SteamClientConnectionInitializer.FromLobby(lobbyId);
        }

        if (hostSteamId != 0)
        {
            label = $"Steam 好友直连（host {hostSteamId}）";
            Diag.Log($"CreateInitializer: 未捕获到大厅 ID，回退 FromPlayer({hostSteamId})");
            return SteamClientConnectionInitializer.FromPlayer(hostSteamId);
        }

        failReason = "既没有主机 Steam ID，也没有 Steam 大厅 ID";
        Diag.Log("CreateInitializer: 主机 Steam ID 与大厅 ID 均缺失，无法建立连接。");
        return null;
    }
}
