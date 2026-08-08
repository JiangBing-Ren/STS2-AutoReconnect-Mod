// =============================================================================
// ReconnectDiagnostics.cs — v0.7.1 双端重连诊断与结果弹窗
// =============================================================================
// 目标：无论重连成功还是失败，客机与主机都要看到「结果 + 原因」。
//
// 为什么需要：
//   在 v0.7.0 之前，重连失败时客机只能看到 RitsuLib 统一的「连接超时」，
//   而真实原因可能是下面任意一种，完全无法区分：
//     · 主机侧僵尸看门狗把刚连上的重连者强断了（1016 Application closed connection）
//     · 大厅 ID 过期 / GetFriendGamePlayed 失效 → InvalidJoin
//     · sessionState 不是 Running（主机已回主菜单 / 还在大厅）
//     · 主机正在战斗中，重连被 ShouldRejectRunningRejoin 拒绝
//     · 握手消息超时（20s 没等到 InitialGameInfo / RejoinResponse）
//   主机侧更是完全没有任何可见反馈——房主根本不知道队友在尝试重连、也不知道为什么失败。
//
// 本文件提供统一的阶段模型 + 中文原因描述 + 原版风格弹窗，供客机与主机两侧共用。
// =============================================================================

using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Steamworks;

namespace AutoReconnect.Scripts;

/// <summary>重连流程的阶段。失败时用于告诉玩家「卡在哪一步」。</summary>
public enum ReconnectStage
{
    /// <summary>尚未开始。</summary>
    Idle,

    /// <summary>解析重连目标（主机 Steam ID / Steam 大厅 ID）。</summary>
    ResolvingTarget,

    /// <summary>建立 Steam P2P + ENet 传输层连接。</summary>
    Connecting,

    /// <summary>等待主机下发 InitialGameInfoMessage（握手第一步）。</summary>
    AwaitingGameInfo,

    /// <summary>校验主机会话状态（必须是 Running 才可重连）。</summary>
    CheckingSession,

    /// <summary>发送 ClientRejoinRequestMessage 并等待主机回执。</summary>
    Handshaking,

    /// <summary>用主机回传的存档重建本地对局 / 把新连接接回 RunManager。</summary>
    RestoringRun,

    /// <summary>全部完成。</summary>
    Completed,
}

/// <summary>
/// 一次重连尝试的失败详情。<c>null</c> 表示成功。
/// v0.7.0 的 <c>PerformReconnectAsync</c> 只返回 bool，失败原因全丢在日志里、
/// 玩家看到的永远是笼统的「连接超时」——这就是为什么三条完全不同的失败路径
/// 在界面上无法区分。改成结构体后，弹窗才能说清「卡在哪一步、为什么」。
/// </summary>
internal readonly struct ReconnectFailure
{
    public readonly ReconnectStage Stage;
    public readonly string Reason;

    public ReconnectFailure(ReconnectStage stage, string reason)
    {
        Stage = stage;
        Reason = string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason;
    }

    public override string ToString() => $"{Stage}: {Reason}";
}

/// <summary>主机侧值得让房主知晓的重连相关事件。</summary>
public enum HostReconnectEvent
{
    /// <summary>某客机掉线，主机开始离线托管倒计时。</summary>
    ClientDisconnected,

    /// <summary>某客机重连握手成功，已回到对局。</summary>
    ClientRejoinAccepted,

    /// <summary>某客机重连被拒（战斗已被托管代打，无法安全恢复）。</summary>
    ClientRejoinRejected,

    /// <summary>某客机 transport 连着但从不握手（Steam 静默重连僵尸），已被强制断开以触发真实重连。</summary>
    ZombieForceDisconnected,
}

/// <summary>
/// 双端重连诊断中枢：阶段描述、原因翻译、结果弹窗。
/// </summary>
internal static class ReconnectDiagnostics
{
    /// <summary>客机是否弹出重连结果弹窗（成功/失败都弹）。可在 ModConfig 中关闭。</summary>
    public static bool ShowClientPopup { get; set; } = true;

    /// <summary>主机是否弹出客机重连事件弹窗。可在 ModConfig 中关闭。</summary>
    public static bool ShowHostPopup { get; set; } = true;

    /// <summary>同一「事件 + 对象」在该时长内不重复弹窗，避免刷屏。</summary>
    private const ulong DedupWindowMs = 8000;

    private static readonly Dictionary<string, ulong> LastShownMs = new();

    // ─────────────────────────────────────────────────────────────
    //  阶段 / 原因 文案
    // ─────────────────────────────────────────────────────────────

    /// <summary>阶段的中文名（用于「卡在第 N 步：xxx」）。</summary>
    public static string StageLabel(ReconnectStage stage) => stage switch
    {
        ReconnectStage.Idle => "准备中",
        ReconnectStage.ResolvingTarget => "第 1 步 · 定位主机",
        ReconnectStage.Connecting => "第 2 步 · 建立网络连接",
        ReconnectStage.AwaitingGameInfo => "第 3 步 · 等待主机下发对局信息",
        ReconnectStage.CheckingSession => "第 4 步 · 校验主机对局状态",
        ReconnectStage.Handshaking => "第 5 步 · 重连握手",
        ReconnectStage.RestoringRun => "第 6 步 · 恢复对局",
        ReconnectStage.Completed => "已完成",
        _ => stage.ToString(),
    };

    /// <summary>阶段失败时的常见成因提示（给玩家一个可执行的下一步）。</summary>
    public static string StageHint(ReconnectStage stage) => stage switch
    {
        ReconnectStage.ResolvingTarget =>
            "既没拿到主机 Steam ID，也没拿到 Steam 大厅 ID。请让房主在设置 → Mods → 自动重连里点「邀请掉线玩家」，或直接从 Steam 好友列表加入。",
        ReconnectStage.Connecting =>
            "Steam P2P 通道没能建立。常见原因：主机已退出对局、大厅已解散、双方网络仍未恢复，或 Steam 好友游戏信息过期。",
        ReconnectStage.AwaitingGameInfo =>
            "连接建立了，但主机始终没有下发对局信息。若主机侧装有旧版本 AutoReconnect，可能是主机的僵尸看门狗把你刚连上的连接强断了（错误码 1016）——请把两端 mod 升到同一版本。",
        ReconnectStage.CheckingSession =>
            "主机当前不在「对局进行中」状态（可能已回到主菜单或还在大厅）。等房主重新进入对局后再试。",
        ReconnectStage.Handshaking =>
            "重连请求已发出但没等到主机回执。可能是主机正忙于战斗结算，或主机侧拒绝了本次重连。",
        ReconnectStage.RestoringRun =>
            "已经握手成功，但用主机回传的存档重建对局时出错。详见 autoreconnect.log。",
        _ => "详见 autoreconnect.log。",
    };

    /// <summary>把 NetError 翻成人话。</summary>
    public static string DescribeNetError(NetError error)
    {
        var name = error.ToString();
        var text = error switch
        {
            NetError.Timeout => "连接超时",
            NetError.Quit => "对方主动退出",
            NetError.RunInProgress => "对局已开始，主机拒绝加入/重连",
            NetError.InvalidJoin => "加入请求无效（大厅已失效或主机好友游戏信息过期）",
            NetError.InternalError => "内部错误",
            _ => name,
        };
        return text == name ? name : $"{text}（{name}）";
    }

    /// <summary>安全地把 NetErrorInfo 翻成人话。</summary>
    public static string DescribeNetErrorInfo(NetErrorInfo info)
    {
        try { return DescribeNetError(info.GetReason()); }
        catch { return "未知网络错误"; }
    }

    /// <summary>把 netId 翻成「玩家名（netId）」，取不到名字就只显示 netId。</summary>
    public static string DescribePeer(ulong netId)
    {
        if (netId == 0) return "未知玩家";
        try
        {
            var name = SteamFriends.GetFriendPersonaName(new CSteamID(netId));
            if (!string.IsNullOrWhiteSpace(name) && name != "[unknown]")
                return $"{name}（{netId}）";
        }
        catch { }
        return $"玩家 {netId}";
    }

    // ─────────────────────────────────────────────────────────────
    //  客机侧结果上报
    // ─────────────────────────────────────────────────────────────

    /// <summary>客机重连成功。</summary>
    public static void ReportClientSuccess(string pathLabel, int attempt, int maxAttempts, string? extra = null)
    {
        var body = $"已重新连回主机的对局。\n\n" +
                   $"重连方式：{pathLabel}\n" +
                   $"尝试次数：第 {attempt} 次（上限 {maxAttempts} 次）";
        if (!string.IsNullOrEmpty(extra)) body += $"\n{extra}";

        Diag.Log($"[Diag] 客机重连成功：{pathLabel}，第 {attempt}/{maxAttempts} 次。{extra}");
        if (!ShowClientPopup) return;
        ShowPopup("重连成功", body, withRetryButton: false, dedupKey: "client-success");
    }

    /// <summary>
    /// 客机重连失败。<paramref name="withRetryButton"/> 为 true 时附加原版风格的「重试」按钮。
    /// </summary>
    public static void ReportClientFailure(
        ReconnectStage stage,
        string reason,
        int attempt,
        int maxAttempts,
        bool withRetryButton = true,
        string? hintOverride = null)
    {
        var hint = hintOverride ?? StageHint(stage);
        var body = $"重连没有成功。\n\n" +
                   $"卡在：{StageLabel(stage)}\n" +
                   $"原因：{reason}\n" +
                   $"尝试次数：{attempt}/{maxAttempts}\n\n" +
                   $"{hint}";

        Diag.Log($"[Diag] 客机重连失败：stage={stage} reason={reason} attempt={attempt}/{maxAttempts}");
        if (!ShowClientPopup) return;
        ShowPopup("重连失败", body, withRetryButton, dedupKey: $"client-fail:{stage}");
    }

    /// <summary>客机被主机明确拒绝（战斗中等），文案与普通失败区分开。</summary>
    public static void ReportClientBlocked(string reason, string hint, bool withRetryButton = true)
    {
        Diag.Log($"[Diag] 客机重连被拒：{reason}");
        if (!ShowClientPopup)
        {
            // 弹窗被关掉时至少保留旧的「等战斗结束」提示通道
            return;
        }
        ShowPopup("暂时无法重连", $"{reason}\n\n{hint}", withRetryButton, dedupKey: $"client-blocked:{reason}");
    }

    // ─────────────────────────────────────────────────────────────
    //  主机侧事件上报
    // ─────────────────────────────────────────────────────────────

    /// <summary>主机侧事件上报：房主也能看到队友的掉线/重连结果与原因。</summary>
    public static void ReportHostEvent(HostReconnectEvent evt, ulong peerId, string detail)
    {
        var who = DescribePeer(peerId);
        var (title, head) = evt switch
        {
            HostReconnectEvent.ClientDisconnected => ("队友掉线", $"{who} 与房间断开了连接。"),
            HostReconnectEvent.ClientRejoinAccepted => ("队友已重连", $"{who} 已成功重连回本局。"),
            HostReconnectEvent.ClientRejoinRejected => ("队友重连被拒", $"{who} 尝试重连，但被拒绝了。"),
            HostReconnectEvent.ZombieForceDisconnected => ("已重置队友连接", $"{who} 的连接处于假死状态，已重置。"),
            _ => ("重连事件", who),
        };

        Diag.Log($"[Diag] 主机事件 {evt}：{who} — {detail}");
        if (!ShowHostPopup) return;

        var body = string.IsNullOrEmpty(detail) ? head : $"{head}\n\n原因：{detail}";
        ShowPopup(title, body, withRetryButton: false, dedupKey: $"host:{evt}:{peerId}");
    }

    // ─────────────────────────────────────────────────────────────
    //  弹窗基建（复用原版 NErrorPopup + NModalContainer）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 以原版 NErrorPopup 样式弹出提示。可选追加「重试」按钮
    /// （复用 <see cref="Ghost.ReconnectBlockedPopup.ScheduleRetryButton"/> 的实现）。
    /// 同一 dedupKey 在 <see cref="DedupWindowMs"/> 内只弹一次。
    /// </summary>
    public static void ShowPopup(string title, string body, bool withRetryButton, string dedupKey)
    {
        try
        {
            ulong now = Time.GetTicksMsec();
            if (LastShownMs.TryGetValue(dedupKey, out var last) && now - last < DedupWindowMs)
            {
                Diag.Log($"[Diag] 弹窗去重：{dedupKey}（{now - last}ms 内重复）");
                return;
            }
            LastShownMs[dedupKey] = now;

            var popup = NErrorPopup.Create(title, body, false);
            var container = NModalContainer.Instance;
            if (popup == null || container == null)
            {
                Diag.Log($"[Diag] 弹窗基建不可用（popup={popup != null}, container={container != null}），仅记录日志：{title} / {body}");
                return;
            }

            container.Add(popup);
            if (withRetryButton)
                Ghost.ReconnectBlockedPopup.ScheduleRetryButton(popup);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Diag] 显示弹窗失败：{ex}");
        }
    }

    /// <summary>换局/重新开始时清空去重表，避免上一局的记录压住新局的弹窗。</summary>
    public static void ResetDedup() => LastShownMs.Clear();
}
