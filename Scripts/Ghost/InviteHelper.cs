// =============================================================================
// InviteHelper.cs — 配置界面“邀请掉线玩家”按钮逻辑
// =============================================================================
// 复用游戏自带的 Steam 邀请对话框（PlatformUtil.OpenInviteDialog），
// 让房主在对局进行中也能把掉线玩家重新邀请回房间。
// 游戏原生的 NInvitePlayersButton 只存在于开局前的 Lobby 界面，
// 对局开始后就消失了，所以这里在配置页提供一个等价按钮。
// =============================================================================

using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Ghost;

/// <summary>
/// 配置页“邀请”按钮背后的动作。房主点击后打开 Steam 邀请对话框。
/// </summary>
internal static class InviteHelper
{
    /// <summary>
    /// 由 ModConfig 按钮的 OnChanged 回调调用。
    /// 仅房主可邀请；非房主 / 非多人局 / 平台不支持时给出提示。
    /// </summary>
    internal static void TryInvite()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm is not { NetService: { } netService })
            {
                Diag.Log("[AutoReconnect] 邀请：RunManager/NetService 为空，当前不在多人游戏中。");
                ShowInfo("无法邀请", "当前不在多人游戏中，无法邀请玩家。");
                return;
            }

            if (netService.Type != NetGameType.Host)
            {
                Diag.Log("[AutoReconnect] 邀请：当前不是房主，跳过。");
                ShowInfo("无法邀请", "只有房主可以邀请玩家回到房间。");
                return;
            }

            if (!PlatformUtil.SupportsInviteDialog(PlatformType.Steam))
            {
                Diag.Log("[AutoReconnect] 邀请：当前平台不支持邀请对话框。");
                ShowInfo("无法邀请", "当前平台不支持邀请对话框。");
                return;
            }

            PlatformUtil.OpenInviteDialog(netService);
            Diag.Log("[AutoReconnect] 已打开 Steam 邀请对话框，请在弹出的好友列表中选择要邀请回房间的玩家。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 打开邀请对话框失败：{ex}");
            ShowInfo("邀请失败", "打开 Steam 邀请对话框时出错，详见日志。");
        }
    }

    /// <summary>
    /// 通用信息弹窗（复用原版 NErrorPopup 样式）。
    /// </summary>
    private static void ShowInfo(string title, string body)
    {
        try
        {
            var popup = NErrorPopup.Create(title, body, false);
            var container = NModalContainer.Instance;
            if (popup != null && container != null)
                container.Add(popup);
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 邀请提示弹窗创建失败：{ex}");
        }
    }
}
