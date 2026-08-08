using System.Collections.Generic;
using System.Text.Json;
using MegaCrit.Sts2.Core.Saves;

namespace AutoReconnect.Scripts.Checkpoint;

/// <summary>
/// 捕获并保存最近的整局检查点（SerializableRun）。
/// 检查点在节点切换边界由 RunSaveManager.SaveRun 写出，本身是干净断点
/// （无“进行中事件/战斗”的半截状态），因此回退到它即可让全员从同一份状态出发。
///
/// 同时为每个检查点生成可读描述（第X幕·第Y层·N人），供对局中常态化检查点浮层
/// （CheckpointHud）渲染可辨识的列表。
/// </summary>
internal static class CheckpointStore
{
    // 最近的几个检查点（按写入顺序），最多保留 N 个，便于“回退到上一个节点”。
    private static readonly List<SerializableRun> _checkpoints = new();
    private static readonly List<string> _labels = new();
    private static readonly object _lock = new();
    private const int MaxCheckpoints = 5;

    public static SerializableRun? Latest => _checkpoints.Count > 0 ? _checkpoints[^1] : null;

    /// <summary>返回全部检查点的只读副本（按写入顺序，索引 0 最早、末尾最新）。</summary>
    public static IReadOnlyList<SerializableRun> GetAll()
    {
        SerializableRun[] snapshot;
        lock (_lock)
        {
            snapshot = _checkpoints.ToArray();
        }
        return snapshot;
    }

    /// <summary>
    /// 返回带可读描述的检查点列表（按写入顺序，seq 从 1 递增）。
    /// CheckpointHud 用此渲染列表；最新检查点 seq 最大。
    /// </summary>
    public static IReadOnlyList<(SerializableRun run, string label, int seq)> GetEntries()
    {
        List<(SerializableRun, string, int)> snapshot;
        lock (_lock)
        {
            snapshot = new List<(SerializableRun, string, int)>(_checkpoints.Count);
            for (int i = 0; i < _checkpoints.Count; i++)
            {
                snapshot.Add((_checkpoints[i], _labels[i], i + 1));
            }
        }
        return snapshot;
    }

    public static void Capture(SerializableRun run)
    {
        if (run == null) return;
        var clone = Clone(run);
        string label = Describe(clone);
        lock (_lock)
        {
            _checkpoints.Add(clone);
            _labels.Add(label);
            while (_checkpoints.Count > MaxCheckpoints)
            {
                _checkpoints.RemoveAt(0);
                _labels.RemoveAt(0);
            }
        }
        Diag.Log($"[Checkpoint] 已捕获检查点（共 {_checkpoints.Count} 个，玩家数 {run.Players?.Count ?? 0}）：{label}");
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _checkpoints.Clear();
            _labels.Clear();
        }
        Diag.Log("[Checkpoint] 检查点已清空");
    }

    /// <summary>
    /// 从 SerializableRun 提取可读描述：第几幕 · 第几层 · 几名玩家。
    /// 仅使用稳定字段，避免反射；楼层用 FloorReached（历史路径总层数）。
    /// </summary>
    private static string Describe(SerializableRun run)
    {
        int act = (run.CurrentActIndex + 1);
        int floor = run.FloorReached;
        int players = run.Players?.Count ?? 0;
        return $"第{act}幕 · 第{floor}层 · {players}名玩家";
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
