using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 全队回退到最近检查点（仅主机调用）。
/// 流程（与 QuickLink 恢复管线一致，已反编译确认）：
///   1. await NGame.Instance.ReturnToMainMenuAfterRun() —— 全员退回主菜单
///   2. new NetHostGameService() + StartSteamHost(玩家数) —— 以检查点重新托管
///   3. NMultiplayerLoadGameScreen.InitializeAsHost(netService, checkpoint) —— 原生多人读档界面
///   4. SubmenuStack.Push(screen) —— 全员加载同一份 SerializableRun
/// 因为全员加载的是同一份干净检查点，状态完全一致，确定性锁步 + ChecksumTracker 满意 → 不再分歧。
/// 掉线玩家重连到本主机（新托管会话）即可加入同一检查点。
/// </summary>
internal static class CheckpointRollback
{
    /// <summary>
    /// Min 版本核心开关：主机检测到客机掉线时，全队回退到最近检查点。
    /// 默认开启（这就是 Min 版本存在的意义）；关闭则退回默认单人重连行为。
    /// </summary>
    public static bool Enabled = true;

    public static async Task RollbackToLatestCheckpoint()
    {
        var checkpoint = CheckpointStore.Latest;
        if (checkpoint == null)
        {
            Diag.Log("[Checkpoint] 无可用检查点，放弃回退。");
            return;
        }
        if (RunManager.Instance is not { NetService: { Type: NetGameType.Host } })
        {
            Diag.Log("[Checkpoint] 非主机或不在对局中，放弃回退。");
            return;
        }

        Diag.Log("[Checkpoint] 开始全队回退到最近检查点（所有人将返回大厅并加载同一检查点）...");

        var game = NGame.Instance;
        if (game == null)
        {
            Diag.Log("[Checkpoint] NGame.Instance 为空，放弃回退。");
            return;
        }

        // 1) 全员退回主菜单
        await game.ReturnToMainMenuAfterRun();

        var mainMenu = game.MainMenu;
        if (mainMenu == null)
        {
            Diag.Log("[Checkpoint] 回退后主菜单为空，放弃。");
            return;
        }

        // 2) 以检查点重新托管
        var netService = new NetHostGameService();
        int maxClients = checkpoint.Players?.Count ?? 4;
        NetErrorInfo? err = await netService.StartSteamHost(maxClients);
        if (err.HasValue)
        {
            Diag.Log($"[Checkpoint] 重新托管失败：{err}");
            return;
        }

        // 3) 原生多人读档界面（用反射绕过 INetGameService / NSubmenu 命名空间）
        var screen = GetSubmenu(mainMenu, typeof(NMultiplayerLoadGameScreen));
        if (screen == null)
        {
            Diag.Log("[Checkpoint] 取 NMultiplayerLoadGameScreen 失败，放弃。");
            return;
        }

        var initMethod = typeof(NMultiplayerLoadGameScreen).GetMethod("InitializeAsHost",
            BindingFlags.Public | BindingFlags.Instance);
        if (initMethod == null)
        {
            Diag.Log("[Checkpoint] 找不到 InitializeAsHost，放弃。");
            return;
        }
        initMethod.Invoke(screen, new object[] { netService, checkpoint });

        // 4) push 到主菜单 submenu 栈
        var stack = mainMenu.SubmenuStack;
        var pushMethod = stack.GetType().GetMethod("Push");
        if (pushMethod == null)
        {
            Diag.Log("[Checkpoint] 找不到 SubmenuStack.Push，放弃。");
            return;
        }
        pushMethod.Invoke(stack, new object[] { screen });

        Diag.Log("[Checkpoint] 已推送检查点读档界面。全队将加载同一检查点；掉线玩家重连到本主机即可加入。");
    }

    private static object? GetSubmenu(NMainMenu mainMenu, Type submenuType)
    {
        var stack = mainMenu.SubmenuStack;
        var getMethod = stack.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetSubmenuType" && m.IsGenericMethodDefinition);
        if (getMethod == null) return null;
        var generic = getMethod.MakeGenericMethod(submenuType);
        return generic.Invoke(stack, null);
    }
}
