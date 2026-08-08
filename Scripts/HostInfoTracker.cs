using System.Reflection;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.2.0 — 捕获联机状态用于重连。
/// 重要修复：不再缓存整个 NetClientGameService 实例（断线后即失效，v0.1.8 的架构错误根因）。
/// 只读取并保留 HostSteamId 等轻量状态；重连时由 ReconnectRunner 新建 NetService。
/// </summary>
internal static class HostInfoTracker
{
    public static CSteamID HostSteamId { get; set; }
    public static bool WasInMultiplayer { get; private set; }
    public static string? LobbyId { get; private set; }
    public static bool IsReconnecting { get; set; }

    /// <summary>
    /// 用户主动退出（菜单/设置）时置 true，避免误触发重连。每次新加入联机时重置。
    /// </summary>
    public static bool DisconnectWasSelfInitiated { get; set; }

    public static void Reset()
    {
        HostSteamId = CSteamID.Nil;
        WasInMultiplayer = false;
        LobbyId = null;
        IsReconnecting = false;
        DisconnectWasSelfInitiated = false;
    }

    /// <summary>
    /// v0.6.1 (Bug A 修复) — 记录客户端连接时的真实 Steam 大厅 ID。
    /// 由 SteamClientConnectToLobbyCapturePatch 在 SteamClient.ConnectToLobby 时调用。
    /// 长断线后 GetFriendGamePlayed 会失败导致 FromPlayer→InvalidJoin；
    /// 保留大厅 ID 后，重连改用 FromLobby 直连即可绕过该检查。
    /// 注意：不在 CaptureFromGame 时清空——大厅 ID 在整局有效，需跨断线保留。
    /// </summary>
    public static void CaptureLobbyId(ulong lobbyId)
    {
        if (lobbyId == 0) return;
        var str = lobbyId.ToString();
        if (LobbyId == str) return;
        LobbyId = str;
        Diag.Log($"CaptureLobbyId: 捕获 Steam 大厅 ID = {str}");
    }

    public static void CaptureFromGame()
    {
        try
        {
            Diag.Log("CaptureFromGame: Starting host info capture...");

            var runManager = RunManager.Instance;
            if (runManager == null)
            {
                Diag.Log("CaptureFromGame: RunManager.Instance is null, abort");
                return;
            }

            var runLobby = GetRunLobby(runManager);
            if (runLobby != null)
            {
                WasInMultiplayer = true;
                Diag.Log("CaptureFromGame: Found lobby, marked WasInMultiplayer=true");

                if (!ExtractHostViaNetService(runLobby))
                {
                    Diag.Log("CaptureFromGame: Could not extract host ID via _netService, trying Players list...");
                    ExtractHostFromPlayers(runLobby);
                }

                if (HostSteamId == CSteamID.Nil)
                {
                    Diag.Log("CaptureFromGame: Host ID still unknown, trying save file...");
                    TryExtractFromSave(runManager);
                }
            }
            else
            {
                Diag.Log("CaptureFromGame: No lobby found on RunManager");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"CaptureFromGame: FAILED - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static object? GetRunLobby(RunManager runManager)
    {
        var runLobbyProp = runManager.GetType().GetProperty("RunLobby",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return runLobbyProp?.GetValue(runManager);
    }

    /// <summary>
    /// v0.2.0 — 只读取 HostNetId，不再缓存整个实例（实例断线后即死）。
    /// </summary>
    private static bool ExtractHostViaNetService(object lobby)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var lobbyType = lobby.GetType();

        var netServiceField = lobbyType.GetField("_netService", flags);
        if (netServiceField == null)
        {
            Diag.Log("ExtractHostViaNetService: _netService field not found");
            return false;
        }

        var netService = netServiceField.GetValue(lobby);
        if (netService == null)
        {
            Diag.Log("ExtractHostViaNetService: _netService is null");
            return false;
        }

        Diag.Log($"ExtractHostViaNetService: _netService type={netService.GetType().FullName}");
        DumpAllMembers(netService, "NetClientGameService");
        return TryExtractHostNetId(netService, "_netService");
    }

    /// <summary>
    /// 尝试从 Players 列表识别 host。
    /// </summary>
    private static void ExtractHostFromPlayers(object lobby)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var playersProp = lobby.GetType().GetProperty("Players", flags);
        if (playersProp == null) return;

        var players = playersProp.GetValue(lobby) as System.Collections.IList;
        if (players == null || players.Count == 0) return;

        Diag.Log($"ExtractHostFromPlayers: {players.Count} players in lobby");

        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null) continue;

            if (i == 0)
            {
                Diag.Log($"--- DUMP PLAYER: {player.GetType().FullName} ---");
                DumpAllMembers(player, "RunLobbyPlayer");
            }

            var playerType = player.GetType();
            foreach (var prop in playerType.GetProperties(flags))
            {
                if (prop.Name.Contains("IsHost") || prop.Name.Contains("isHost"))
                {
                    try
                    {
                        if (prop.GetValue(player) is true)
                        {
                            Diag.Log($"  Player {i} is host! (by {prop.Name})");
                            TryExtractSteamIdFromPlayer(player, i);
                        }
                    }
                    catch { }
                }
                if (prop.Name == "NetId" || prop.Name == "SteamId" || prop.Name == "AccountId")
                {
                    try
                    {
                        var val = prop.GetValue(player);
                        Diag.Log($"  Player {i}.{prop.Name} = {val}");
                    }
                    catch { }
                }
            }

            foreach (var field in playerType.GetFields(flags))
            {
                if (field.Name.Contains("IsHost") || field.Name.Contains("isHost") ||
                    field.Name == "NetId" || field.Name == "SteamId")
                {
                    try
                    {
                        var val = field.GetValue(player);
                        Diag.Log($"  Player {i}.{field.Name} (field) = {val}");
                    }
                    catch { }
                }
            }
        }
    }

    private static void TryExtractSteamIdFromPlayer(object player, int index)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = player.GetType();

        foreach (var name in new[] { "NetId", "SteamId", "AccountId", "CSteamID" })
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null)
            {
                try
                {
                    var value = prop.GetValue(player);
                    if (value != null)
                    {
                        HostSteamId = ConvertToCSteamID(value);
                        Diag.Log($"Host ID from Player[{index}].{name} = {HostSteamId}");
                        return;
                    }
                }
                catch { }
            }

            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                try
                {
                    var value = field.GetValue(player);
                    if (value != null)
                    {
                        HostSteamId = ConvertToCSteamID(value);
                        Diag.Log($"Host ID from Player[{index}].{name} (field) = {HostSteamId}");
                        return;
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Dump all properties AND fields of an object for diagnosis.
    /// </summary>
    private static void DumpAllMembers(object obj, string label)
    {
        if (obj == null) return;
        var type = obj.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Diag.Log($"--- DUMP {label} ({type.FullName}) ---");

        foreach (var prop in type.GetProperties(flags))
        {
            try
            {
                var value = prop.GetValue(obj);
                var valStr = value == null ? "null" : $"{value.GetType().Name}: {value}";
                Diag.Log($"  PROP {prop.Name} ({prop.PropertyType.Name}) = {valStr}");
            }
            catch (Exception ex)
            {
                Diag.Log($"  PROP {prop.Name} ({prop.PropertyType.Name}) = ERROR: {ex.Message}");
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            try
            {
                var value = field.GetValue(obj);
                var valStr = value == null ? "null" : $"{value.GetType().Name}: {value}";
                Diag.Log($"  FIELD {field.Name} ({field.FieldType.Name}) = {valStr}");
            }
            catch (Exception ex)
            {
                Diag.Log($"  FIELD {field.Name} ({field.FieldType.Name}) = ERROR: {ex.Message}");
            }
        }

        Diag.Log($"--- END DUMP {label} ---");
    }

    private static bool TryExtractHostNetId(object obj, string source)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = obj.GetType();

        var hostNetProp = type.GetProperty("HostNetId", flags);
        if (hostNetProp != null)
        {
            try
            {
                var value = hostNetProp.GetValue(obj);
                if (value != null)
                {
                    HostSteamId = ConvertToCSteamID(value);
                    Diag.Log($"HostNetId found via {source}.HostNetId = {HostSteamId}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"  {source}.HostNetId threw: {ex.Message}");
            }
        }

        var hostNetField = type.GetField("_hostNetId", flags);
        if (hostNetField != null)
        {
            try
            {
                var value = hostNetField.GetValue(obj);
                if (value != null)
                {
                    HostSteamId = ConvertToCSteamID(value);
                    Diag.Log($"HostNetId found via {source}._hostNetId = {HostSteamId}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"  {source}._hostNetId threw: {ex.Message}");
            }
        }

        foreach (var name in new[] { "HostId", "HostSteamId", "HostCSteamID", "SteamId" })
        {
            var prop = type.GetProperty(name, flags);
            if (prop == null) continue;
            try
            {
                var value = prop.GetValue(obj);
                if (value != null)
                {
                    HostSteamId = ConvertToCSteamID(value);
                    Diag.Log($"Host ID found via {source}.{name} = {HostSteamId}");
                    return true;
                }
            }
            catch { }
        }

        foreach (var name in new[] { "_hostNetId", "_hostId", "_hostSteamId" })
        {
            var field = type.GetField(name, flags);
            if (field == null) continue;
            try
            {
                var value = field.GetValue(obj);
                if (value != null)
                {
                    HostSteamId = ConvertToCSteamID(value);
                    Diag.Log($"Host ID found via {source}.{name} (field) = {HostSteamId}");
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    private static CSteamID ConvertToCSteamID(object value)
    {
        if (value is CSteamID sid) return sid;
        if (value is ulong ul) return new CSteamID(ul);
        if (value is long l) return new CSteamID((ulong)l);
        if (value is uint ui) return new CSteamID(ui);
        if (value is int i) return new CSteamID((uint)i);

        var str = value.ToString();
        if (ulong.TryParse(str, out var parsed))
            return new CSteamID(parsed);

        return CSteamID.Nil;
    }

    private static void TryExtractFromSave(RunManager runManager)
    {
        Diag.Log("TryExtractFromSave: Attempting to recover host ID from save data...");
        try
        {
            var currentRunProp = runManager.GetType().GetProperty("CurrentRun",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentRunProp != null)
            {
                var currentRun = currentRunProp.GetValue(runManager);
                if (currentRun != null)
                {
                    Diag.Log($"TryExtractFromSave: CurrentRun type={currentRun.GetType().FullName}");
                    TryExtractHostNetId(currentRun, "CurrentRun");
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"TryExtractFromSave: Error - {ex.Message}");
        }
    }
}
