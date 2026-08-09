using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// Min 版本核心行为：主机检测到客机掉线时，弹出“邀请 / 回退”提示，【不再】自动回退。
/// 挂在 RunLobby.OnDisconnectedFromClientAsHost（主机侧、客机传输层超时约 14s 后触发）。
/// 与 OfflineTakeover 的 Ghost 托管是两条不同路径：本补丁只负责“通知 + 给主机选项”，
/// 真正回退由主机在弹窗里点“回退到检查点”触发（CheckpointRollback.RollbackTo），
/// 从根本上消除“重连落点带入进行中房间”导致的 StateDivergence。
///
/// 同一掉线玩家只弹一次提示（按 Steam ID 去重）；队友通过邀请重连成功后清除去重标记，
/// 以便后续再次掉线仍能提示（见 CheckpointReconnectClearPatch）。
/// </summary>
[HarmonyPatch(typeof(RunLobby), "OnDisconnectedFromClientAsHost")]
internal static class CheckpointRollbackOnHostDisconnectPatch
{
    public static void Postfix(ulong playerId, object info)
    {
        try
        {
            if (!CheckpointRollback.Enabled) return;

            // 解析断线原因（反射调用 NetErrorInfo.GetReason，避免直接依赖该类型）
            NetError reason = NetError.Quit;
            if (info != null)
            {
                try
                {
                    var reasonMethod = info.GetType().GetMethod("GetReason",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (reasonMethod != null)
                    {
                        var r = reasonMethod.Invoke(info, null);
                        if (r is NetError re) reason = re;
                    }
                }
                catch { }
            }

            // RunInProgress = 主机拒绝运行中重连所致，非真实掉线；据此提示会与拒绝形成循环，跳过。
            if (reason == NetError.RunInProgress)
            {
                Diag.Log($"[Checkpoint] 忽略 {playerId} 的 RunInProgress 断线，不弹提示。");
                return;
            }

            // OnDisconnectedFromClientAsHost 本就只在主机触发；再确认一次以防万一。
            if (RunManager.Instance is not { NetService: { Type: NetGameType.Host } })
            {
                Diag.Log("[Checkpoint] 非主机上下文，跳过提示。");
                return;
            }

            // 同一玩家只提示一次，避免重复弹窗。
            if (!CheckpointRollback.TryMarkNotified(playerId))
            {
                Diag.Log($"[Checkpoint] 玩家 {playerId} 已提示过掉线，忽略重复触发。");
                return;
            }

            Diag.Log($"[Checkpoint] 主机检测到玩家 {playerId} 掉线（{reason}），弹出邀请/回退提示。");
            CheckpointRollbackPopup.Show(playerId);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 弹窗触发异常：{ex}");
            CheckpointRollback.ClearNotified(playerId);
        }
    }
}

/// <summary>
/// 主机侧客户端（重）连接成功时触发：清除该玩家的“已提示”标记，
/// 使后续若再次掉线仍能正常弹出邀请/回退提示。
/// </summary>
[HarmonyPatch(typeof(RunLobby), "OnConnectedToClientAsHost")]
internal static class CheckpointReconnectClearPatch
{
    public static void Postfix(ulong playerId)
    {
        try
        {
            CheckpointRollback.ClearNotified(playerId);
            Diag.Log($"[Checkpoint] 玩家 {playerId} 已（重）连接，清除掉线提示去重标记。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 清除掉线标记异常：{ex}");
        }
    }
}
