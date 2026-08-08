using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts;

/// <summary>
/// Captures host info when a multiplayer run is set up.
/// Triggered on both fresh starts and loaded runs.
/// </summary>
[HarmonyPatch(typeof(RunManager), "SetUpSavedMultiplayer")]
internal static class SetUpSavedMultiplayerPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        Diag.Log("SetUpSavedMultiplayer patch fired!");
        HostInfoTracker.CaptureFromGame();
    }
}

/// <summary>
/// Captures host info when a run is launched (covers edge cases).
/// </summary>
[HarmonyPatch(typeof(RunManager), "Launch")]
internal static class RunLaunchedPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        Diag.Log("Launch patch fired!");
        HostInfoTracker.CaptureFromGame();
    }
}

/// <summary>
/// v0.6.1 (Bug A 修复) — 捕获真实的 Steam 大厅 ID。
/// 客户端每次连接大厅都会经过 SteamClient.ConnectToLobby(ulong lobbyId, CancellationToken)：
///   - 通过好友加入：ConnectToLobbyOwnedByFriend → ConnectToLobby(真实大厅ID)
///   - 直接加入大厅：SteamClientConnectionInitializer.FromLobby → ConnectToLobby
/// 长断线后 SteamFriends.GetFriendGamePlayed 会失败（主机好友游戏信息不再报告有效 STS2 大厅），
/// 使 FromPlayer 路径在 ConnectToLobbyOwnedByFriend 直接返回 InvalidJoin。
/// 捕获此 lobbyId 后，重连改用 FromLobby(lobbyId) 直连，跳过 GetFriendGamePlayed 检查。
/// </summary>
[HarmonyPatch]
internal static class SteamClientConnectToLobbyCapturePatch
{
    [HarmonyTargetMethod]
    private static MethodInfo? TargetMethod()
    {
        var steamClientType = System.Type.GetType(
            "MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient, MegaCrit.Sts2.Core",
            throwOnError: false)
            ?? AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient");
        if (steamClientType == null) return null;

        return AccessTools.Method(steamClientType, "ConnectToLobby",
                   new[] { typeof(ulong), typeof(System.Threading.CancellationToken) })
               ?? AccessTools.Method(steamClientType, "ConnectToLobby");
    }

    [HarmonyPrefix]
    private static void Prefix(ulong lobbyId)
    {
        try
        {
            HostInfoTracker.CaptureLobbyId(lobbyId);
        }
        catch { }
    }
}

/// <summary>
/// v0.1.8 — Track whether the disconnect was user-initiated (normal exit)
/// to avoid triggering reconnection when the user deliberately exits.
/// Patches SteamClient.DisconnectFromHostInternal which has a selfInitiated bool.
/// </summary>
[HarmonyPatch]
internal static class DisconnectSelfInitiatedPatch
{
    [HarmonyTargetMethod]
    private static MethodInfo TargetMethod()
    {
        var steamClientType = System.Type.GetType(
            "MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient, MegaCrit.Sts2.Core",
            throwOnError: false);
        if (steamClientType != null)
        {
            var method = steamClientType.GetMethod("DisconnectFromHostInternal",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null) return method;
        }

        // Fallback: resolve at patch time
        return AccessTools.Method("MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient:DisconnectFromHostInternal");
    }

    [HarmonyPrefix]
    private static void Prefix(object[] __args)
    {
        try
        {
            // __args contains: (SteamDisconnectionReason reason, string debugReason, bool now, bool selfInitiated)
            // The selfInitiated param is the last bool
            if (__args.Length >= 4 && __args[3] is bool selfInitiated && selfInitiated)
            {
                Diag.Log("DisconnectSelfInitiated: User-initiated disconnect detected, suppressing reconnection");
                HostInfoTracker.DisconnectWasSelfInitiated = true;
            }
        }
        catch { }
    }
}

/// <summary>
/// v0.1.8 — Patches RunManager.LocalPlayerDisconnected(NetErrorInfo) instead of
/// ReturnToMainMenuWithError. This is more precise: only fires on actual disconnects
/// (not other error scenarios), and we can check the error info.
/// </summary>
[HarmonyPatch(typeof(RunManager), "LocalPlayerDisconnected")]
internal static class LocalPlayerDisconnectedPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NetErrorInfo? info)
    {
        try
        {
            Diag.Log($"LocalPlayerDisconnected fired: WasInMultiplayer={HostInfoTracker.WasInMultiplayer} " +
                     $"IsReconnecting={HostInfoTracker.IsReconnecting} SelfInitiated={HostInfoTracker.DisconnectWasSelfInitiated}");

            // If user deliberately exited, let the normal flow continue
            if (HostInfoTracker.DisconnectWasSelfInitiated)
            {
                Diag.Log("User-initiated exit, allowing normal flow");
                HostInfoTracker.Reset();
                return true;
            }

            if (!HostInfoTracker.WasInMultiplayer)
            {
                Diag.Log("Not multiplayer, allowing normal flow");
                return true;
            }

            if (HostInfoTracker.IsReconnecting)
            {
                Diag.Log("Already reconnecting, blocking");
                return false;
            }

            // Log NetErrorInfo for diagnosis
            if (info != null)
            {
                try
                {
                    // GetReason() exists but may throw — call via reflection
                    var reasonMethod = info.GetType().GetMethod("GetReason",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (reasonMethod != null)
                    {
                        var reason = reasonMethod.Invoke(info, null);
                        Diag.Log($"NetErrorInfo reason: {reason}");
                    }
                    else
                    {
                        Diag.Log($"NetErrorInfo: {info}");
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log($"NetErrorInfo.GetReason() threw: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            else
            {
                Diag.Log("NetErrorInfo is null");
            }

            Diag.Log("Unexpected disconnect — starting reconnection flow");

            // v0.7.2 —— 自动重连总开关。关闭时，放行原版 LocalPlayerDisconnected 流程，
            // 退回主菜单，由 MenuRejoinFlow 走主机存档对局（原生读档）重建，而非对局内自动重连。
            // 适用于“状态已分歧（如 StateDivergence），想干净重连”的场景。
            if (!ReconnectService.AutoReconnectEnabled)
            {
                Diag.Log("[AutoReconnect] 自动重连已关闭，放行原版流程退回主菜单（MenuRejoin 语境）。");
                return true; // 不阻断原版流程 → 玩家回到主菜单，可走 MenuRejoin 原生读档重连
            }

            HostInfoTracker.IsReconnecting = true;

            var reconnectNode = new ReconnectRunner();
            reconnectNode.Name = "AutoReconnectMin_Runner";
            // 断线瞬间 NetService 仍在，捕获主机 Steam ID 供 FromPlayer 重建连接。
            try
            {
                if (RunManager.Instance is { NetService: MegaCrit.Sts2.Core.Multiplayer.NetClientGameService csv })
                    reconnectNode.HostSteamId = csv.HostNetId;
            }
            catch { }
            AddToSceneTree(reconnectNode);

            return false; // Block normal LocalPlayerDisconnected flow
        }
        catch (Exception ex)
        {
            Diag.Log($"LocalPlayerDisconnected patch exception: {ex}");
            return true;
        }
    }

    private static void AddToSceneTree(Godot.Node node)
    {
        var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        tree?.Root.AddChild(node);
    }
}
