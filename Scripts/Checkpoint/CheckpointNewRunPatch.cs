using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 开新对局时清空上一对局残留的检查点。
///
/// 只 Hook 真正“新建”对局的两个入口：
///   - RunManager.SetUpNewSingleplayer
///   - RunManager.SetUpNewMultiplayer
/// 检查点回退 / 读档续局走的是 SetUpSaved*（内部调用 InitializeSavedRun，不触发本补丁），
/// 因此回退链上更早的检查点会被保留（仍可继续回退到更早节点）。
///
/// 之所以用 Prefix 清空：新对局的首个 SaveRun 在 Setup 之后的节点边界触发，
/// 清空发生在它之前，保证新对局的检查点列表是干净起点。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer))]
internal static class CheckpointNewRunSingleplayerPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        try
        {
            CheckpointStore.Clear();
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 新对局清空检查点异常（已忽略）：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewMultiplayer))]
internal static class CheckpointNewRunMultiplayerPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        try
        {
            CheckpointStore.Clear();
        }
        catch (Exception ex)
        {
            Diag.Log($"[Checkpoint] 新对局清空检查点异常（已忽略）：{ex.Message}");
        }
    }
}
