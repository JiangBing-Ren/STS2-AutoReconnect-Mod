using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 捕获检查点：每次 RunSaveManager.SaveRun(可序列化整局, 是否多人) 写出整局时，
/// 深拷贝一份 SerializableRun 存为最近检查点。
/// SaveRun 在节点切换边界触发，因此检查点是干净断点（无进行中事件/战斗的半截状态）。
/// 这正是 QuickLink 复用游戏原生存档机制的做法——不自己发明序列化。
/// </summary>
[HarmonyPatch(typeof(RunSaveManager), "SaveRun", new[] { typeof(SerializableRun), typeof(bool) })]
internal static class CheckpointCapturePatch
{
    [HarmonyPostfix]
    private static void Postfix(SerializableRun save)
    {
        try
        {
            if (save == null) return;
            CheckpointStore.Capture(save);
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 捕获检查点异常（已忽略）：{ex.Message}");
        }
    }
}
