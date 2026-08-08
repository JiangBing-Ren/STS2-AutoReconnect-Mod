using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using AutoReconnect.Scripts.Ghost;

namespace AutoReconnect.Scripts.MenuRejoin;

/// <summary>
/// v0.7.0 —— 接管 <c>JoinFlow.Begin</c> 的返回结果，实现「从主菜单重连进对局」。
///
/// ## 为什么 patch 的是 JoinFlow.Begin 而不是 NJoinFriendScreen.JoinGameAsync
///
/// 真正写死拒绝逻辑的是 <c>JoinGameAsync</c>，但它是 async 方法，方法体在编译器生成的
/// 状态机里，只能靠 Transpiler 改 IL，既脆弱又难维护。
///
/// 换个角度：<c>JoinGameAsync</c> 的三个分支判断都是
/// <c>if (joinResult.sessionState == RunSessionState.XXX)</c>，而
/// <c>JoinResult.sessionState</c> 的类型是 <c>RunSessionState?</c>。
/// 只要我们在它拿到结果之前把 <c>sessionState</c> 置为 <c>null</c>，三个 if 就会
/// **全部落空**，方法直接静默走到 finally（关掉 loading 遮罩）—— 既不弹错误弹窗、
/// 也不会调 <c>Disconnect(RunInProgress)</c>。原版还有个 default 分支会 throw，
/// 但那在 switch 里（JoinFlow 内部），跟这里的 if 链无关。
///
/// 所以这里用 Postfix 把 <c>Begin</c> 返回的 Task 包一层：await 原 Task 拿到结果，
/// 自己完成重连，再把 sessionState 抹掉交回去。附带好处是 —— 我们的逻辑跑在
/// <c>Begin</c> 的 finally **之后**，那时 JoinFlow 已经注销掉自己的消息处理器、
/// 解绑了 Disconnected 事件，NetService 是干净的，正好移交给 RunManager。
///
/// 对其它 sessionState（InLobby / InLoadedLobby）我们原样放行，零影响。
/// </summary>
[HarmonyPatch(typeof(JoinFlow), nameof(JoinFlow.Begin))]
internal static class JoinFlowRejoinPatch
{
    private static bool Prepare()
    {
        // 目标方法不存在（游戏改版）时安静跳过，不影响其它补丁。
        return AccessTools.Method(typeof(JoinFlow), nameof(JoinFlow.Begin)) != null;
    }

    [HarmonyPostfix]
    private static void Postfix(JoinFlow __instance, ref Task<JoinResult> __result)
    {
        var inner = __result;
        if (inner == null) return;
        __result = InterceptAsync(__instance, inner);
    }

    internal static async Task<JoinResult> InterceptAsync(JoinFlow flow, Task<JoinResult> inner)
    {
        // 原流程的异常（版本不符、mod 不匹配、被主机踢等）必须原样抛出，
        // 交给 JoinGameAsync 的 catch 去弹对应的错误弹窗。
        var result = await inner.ConfigureAwait(true);

        try
        {
            if (result.sessionState != RunSessionState.Running) return result;
            if (!result.rejoinResponse.HasValue) return result;

            MenuRejoinFlow.LastAttemptWasRejoin = true;

            // 本端已经有一局在跑 —— 说明这是「对局内断线」，客机内存里还留着完整的
            // run（含战斗现场），交给 ReconnectRunner 走原有的轻量重连路径即可，
            // 千万不要在这里重建（会撞上 "State is already set."）。
            if (RunManager.Instance.DebugOnlyGetState() != null)
            {
                Diag.Log("[MenuRejoin] 本端已有进行中的 run，交给 ReconnectRunner 处理。");
                return result;
            }

            var rejoin = result.rejoinResponse.Value;
            var myId = flow.NetService.NetId;

            // 我本来就不是这局的玩家 → 这是「围观别人已开始的对局」，
            // 原版拒绝是对的，放行让它弹 RunInProgress。
            var players = rejoin.serializableRun?.Players;
            if (players == null || players.All(p => p.NetId != myId))
            {
                Diag.Log($"[MenuRejoin] {myId} 不在本局玩家名单中，交回原版处理。");
                return result;
            }

            // 记下主机 Steam ID，供「重试」按钮在战斗未结束 / 重建失败时重新握手。
            try
            {
                if (flow.NetService is NetClientGameService ncg)
                    MenuRejoinFlow.PendingRejoinHostSteamId = ncg.HostNetId;
            }
            catch { }

            // 主机正在战斗中：NetFullCombatState 只是只读快照，没有恢复战斗现场的能力，
            // 硬重建必然状态分歧。提示玩家等战斗结束，并附「重试」按钮。
            if (MenuRejoinFlow.HostIsInCombat(rejoin.combatState))
            {
                Diag.Log("[MenuRejoin] 主机正处于战斗中，本次重连改为提示等待。");
                ReconnectDiagnostics.ReportClientBlocked(
                    "主机正处于战斗中。",
                    "游戏本身不具备「从战斗快照恢复战斗现场」的能力（主机回传的 combatState 只是只读校验快照），" +
                    "强行重建必然与主机状态分歧。请等本场战斗结束后点「重试」。");
                try { flow.NetService.Disconnect(NetError.RunInProgress); } catch { }
                // 抹掉 sessionState，避免原版再弹一次英文的 RunInProgress 弹窗（双弹窗）。
                result.sessionState = null;
                return result;
            }

            try
            {
                await MenuRejoinFlow.EnterRunFromRejoin(flow.NetService, rejoin).ConfigureAwait(true);

                // 重连成功。抹掉 sessionState，让 JoinGameAsync 的三个 if 全部落空、静默收尾。
                result.sessionState = null;
                MenuRejoinFlow.LastAttemptWasRejoin = false;
                MenuRejoinFlow.PendingRejoinHostSteamId = 0;

                ReconnectDiagnostics.ReportClientSuccess(
                    "主菜单重连（用主机回传的存档重建对局）",
                    attempt: 1,
                    maxAttempts: 1,
                    $"已恢复到第 {(rejoin.serializableRun?.CurrentActIndex ?? 0) + 1} 幕，" +
                    $"共 {rejoin.serializableRun?.Players.Count ?? 0} 名玩家。");
            }
            catch (Exception ex)
            {
                // 重建中途失败：EnterRunFromRejoin 的 catch 已清理半途的 RunManager 状态，
                // 这里直接弹中文提示 + 重试（PendingRejoinHostSteamId 仍有效 → 走菜单重试）。
                // 不依赖 JoinGameAsync 弹英文弹窗，避免双弹窗与语境错乱。
                Diag.Log($"[MenuRejoin] 重建对局失败，弹提示等待重试：{ex}");
                ReconnectDiagnostics.ReportClientFailure(
                    ReconnectStage.RestoringRun,
                    $"{ex.GetType().Name}: {ex.Message}",
                    attempt: 1,
                    maxAttempts: 1,
                    withRetryButton: true);
                try { flow.NetService.Disconnect(NetError.InternalError); } catch { }
                result.sessionState = null;
            }
        }
        catch (Exception ex)
        {
            // 保持 result 原样返回 → 退回原版行为（弹 RunInProgress + 断开），
            // 至少玩家能看到明确的失败提示，而不是卡在一个半死不活的界面。
            Diag.Log($"[MenuRejoin] 重建对局失败，退回原版处理：{ex}");
        }

        return result;
    }
}
