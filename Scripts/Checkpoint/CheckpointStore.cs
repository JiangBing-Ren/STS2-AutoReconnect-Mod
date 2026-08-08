using System.Collections.Generic;
using System.Text.Json;
using MegaCrit.Sts2.Core.Saves;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 捕获并保存最近的整局检查点（SerializableRun）。
/// 检查点在节点切换边界由 RunSaveManager.SaveRun 写出，本身是干净断点
/// （无“进行中事件/战斗”的半截状态），因此回退到它即可让全员从同一份状态出发。
/// </summary>
internal static class CheckpointStore
{
    // 最近的几个检查点（按写入顺序），最多保留 N 个，便于“回退到上一个节点”。
    private static readonly List<SerializableRun> _checkpoints = new();
    private const int MaxCheckpoints = 5;

    public static SerializableRun? Latest => _checkpoints.Count > 0 ? _checkpoints[^1] : null;

    public static void Capture(SerializableRun run)
    {
        if (run == null) return;
        _checkpoints.Add(Clone(run));
        while (_checkpoints.Count > MaxCheckpoints) _checkpoints.RemoveAt(0);
        Diag.Log($"[Checkpoint] 已捕获检查点（共 {_checkpoints.Count} 个，玩家数 {run.Players?.Count ?? 0}）");
    }

    public static void Clear()
    {
        _checkpoints.Clear();
        Diag.Log("[Checkpoint] 检查点已清空");
    }

    /// <summary>
    /// 深拷贝：用游戏自己的序列化器（JsonSerializationUtility.GetTypeInfo）做 JSON 往返，
    /// 避免持有游戏后续会变更的同一对象引用。SaveRun 触发不频繁（节点边界），开销可接受。
    /// </summary>
    private static SerializableRun Clone(SerializableRun run)
    {
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var json = JsonSerializer.Serialize(run, typeInfo);
        return JsonSerializer.Deserialize<SerializableRun>(json, typeInfo)!;
    }
}
