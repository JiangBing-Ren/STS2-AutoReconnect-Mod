using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// Min 版本核心行为：主机检测到客机掉线时，全队回退到最近检查点。
/// 挂在 RunLobby.OnDisconnectedFromClientAsHost（主机侧、客机传输层超时约 14s 后触发）。
/// 与 OfflineTakeover 的 Ghost 托管是两条不同路径：本补丁直接把全队拽回最近干净节点重打，
/// 从根本上消除“重连落点带入进行中房间”导致的 StateDivergence。
/// 回退本身由 CheckpointRollback.RollbackToLatestCheckpoint 在后台线程执行（与 QuickLink 一致）。
/// </summary>
[HarmonyPatch(typeof(RunLobby), "OnDisconnectedFromClientAsHost")]
internal static class CheckpointRollbackOnHostDisconnectPatch
{
    private static bool _rollingBack;

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
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (reasonMethod != null)
                    {
                        var r = reasonMethod.Invoke(info, null);
                        if (r is NetError re) reason = re;
                    }
                }
                catch { }
            }

            // RunInProgress = 主机拒绝运行中重连所致，非真实掉线；据此回退会与拒绝形成循环，跳过。
            if (reason == NetError.RunInProgress)
            {
                Diag.Log($"[Checkpoint] 忽略 {playerId} 的 RunInProgress 断线，不触发回退。");
                return;
            }

            // OnDisconnectedFromClientAsHost 本就只在主机触发；再确认一次以防万一。
            if (RunManager.Instance is not { NetService: { Type: NetGameType.Host } })
            {
                Diag.Log("[Checkpoint] 非主机上下文，跳过回退触发。");
                return;
            }

            if (_rollingBack)
            {
                Diag.Log("[Checkpoint] 已有回退在进行，忽略本次掉线触发。");
                return;
            }
            if (CheckpointStore.Latest == null)
            {
                Diag.Log("[Checkpoint] 尚无可回退检查点，忽略本次掉线触发。");
                return;
            }

            _rollingBack = true;
            Diag.Log($"[Checkpoint] 主机检测到玩家 {playerId} 掉线（{reason}），触发全队回退到最近检查点。");
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckpointRollback.RollbackToLatestCheckpoint();
                }
                catch (Exception ex)
                {
                    Diag.Log($"[Checkpoint] 回退执行异常：{ex}");
                }
                finally
                {
                    _rollingBack = false;
                }
            });
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 触发回退异常：{ex}");
            _rollingBack = false;
        }
    }
}
