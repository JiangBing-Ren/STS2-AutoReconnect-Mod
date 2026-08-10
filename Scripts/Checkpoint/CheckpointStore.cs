using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
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

    // 上一个已存检查点的节点坐标键，用于“商店/非战斗事件只存进入检查点”的同节点去重。
    private static string? _lastNodeKey;

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

        // 商店 / 非战斗事件（含问号分支战斗）只保留“进入”检查点：
        // SaveRun 在节点“进入”与“完成”时各触发一次，同一节点两次保存的坐标相同；
        // 第二次（完成/离开）对这类安全、可重选的节点无意义，跳过以免占用宝贵缓冲位。
        // 分类依据 MapPointType（地图生成类型）：问号分支成战斗仍保持 Unknown，故其仍属“非战斗事件”。
        string? key = CurrentNodeKey(run);
        bool onlyEnter = TryGetNodeFromHistory(run, out var mapType, out _) && IsOnlyEnterNode(mapType);
        if (onlyEnter && key != null && key == _lastNodeKey)
        {
            Diag.Log($"[Checkpoint] 跳过{NodeTypeName(mapType)}节点完成时的重复检查点（仅保留进入）：{key}");
            return;
        }

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
        _lastNodeKey = key;
        Diag.Log($"[Checkpoint] 已捕获检查点（共 {_checkpoints.Count} 个，玩家数 {run.Players?.Count ?? 0}，分类={NodeTypeName(mapType)}）：{label}");
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
    /// 从 SerializableRun 提取可读描述（两行）：
    ///   第1行：节点类型 · 地图坐标（col,row）
    ///   第2行：第X幕 · 第Y层 · N人 · 金币G · 卡C
    /// 楼层 = 权威坐标 coord.row + 1（游戏标准算法，见 RunManager.EnterMapPointInternal），
    /// 不再用 FloorReached（历史条目累计数，SaveRun 瞬间滞后且同一节点多次 SaveRun 会重复累计）。
    /// 节点类型 / 坐标优先运行时 RunState.CurrentMapPoint / CurrentMapCoord（由坐标反查地图生成类型，
    /// 在 SaveRun 瞬间已更新、不滞后），见 TryGetNodeFromHistory。
    /// </summary>
    private static string Describe(SerializableRun run)
    {
        int act = run.CurrentActIndex + 1;
        // 楼层：游戏标准 = 权威坐标 row + 1（RunManager.cs:870 即 coord.row + 1 作为 actFloor）。
        int floor = GetLiveCoord(run, out var fc) ? fc.row + 1 : run.FloorReached;
        int players = run.Players?.Count ?? 0;

        // 节点类型 + 坐标：优先用快照 MapPointHistory 最后一个条目的类型 + VisitedMapCoords
        // 最后一个坐标。历史条目类型反映玩家实际进入/完成的房间类型（例如“先古之民”事件选择
        // 后进入战斗，历史条目会更新为 Monster）；这比 Map.GetPoint(coord).PointType 更可靠，
        // 后者只是地图生成类型，事件节点被覆盖为战斗房间后仍返回 Ancient，造成类型和实际节点
        // 错配。失败才回退旧逻辑。
        string node = "未知节点";
        string coord = "";
        if (TryGetNodeFromHistory(run, out var mapType, out var liveCoord))
        {
            // 显示用“地图生成类型”：CurrentMapPoint.PointType 由坐标反查，在 SaveRun 瞬间已更新、
            // 不滞后。问号(Unknown)→事件，先古(Ancient)→远古，其余按地图类型直接显示。
            node = DisplayNodeType(mapType);
            coord = liveCoord;
        }
        else
        {
            // 兜底：无历史条目（初始检查点）时按地图生成类型 + 坐标。
            try
            {
                var history = run.MapPointHistory;
                if (history != null && run.CurrentActIndex >= 0 && run.CurrentActIndex < history.Count)
                {
                    var actHistory = history[run.CurrentActIndex];
                    if (actHistory is { Count: > 0 })
                        node = NodeTypeName(actHistory[^1].MapPointType);
                }
            }
            catch { /* 忽略，使用默认 */ }

            try
            {
                if (run.VisitedMapCoords is { Count: > 0 } coords)
                {
                    var c = coords[^1];
                    coord = $"坐标({c.col},{c.row})";
                }
            }
            catch { /* 忽略 */ }
        }

        // 金币 / 卡牌数（取首个玩家作代表，字段缺失则忽略）
        string extra = "";
        try
        {
            if (run.Players is { Count: > 0 } ps && ps[0] != null)
            {
                int gold = ps[0].Gold;
                int cards = ps[0].Deck?.Count ?? 0;
                extra = $" · 金币{gold} · 卡{cards}";
            }
        }
        catch { /* 忽略 */ }

        string line1 = string.IsNullOrEmpty(coord) ? node : $"{node} · {coord}";
        return $"{line1}\n第{act}幕·第{floor}层 · {players}人{extra}";
    }

    /// <summary>
    /// 运行时优先的权威坐标：<c>state.CurrentMapCoord</c>（= VisitedMapCoords.Last()，与 HUD 同款、不滞后）。
    /// 运行时不可用时回退快照 <c>run.VisitedMapCoords[^1]</c>。
    /// 这是检查点“楼层 / 坐标 / 类型”的唯一真相源（见下方 TryGetNodeFromHistory 说明）。
    /// </summary>
    private static bool GetLiveCoord(SerializableRun run, out MapCoord coord)
    {
        coord = default;
        try
        {
            var rm = RunManager.Instance;
            var state = rm != null ? GetRunState(rm) : null;
            if (state?.CurrentMapCoord is { } live && state.VisitedMapCoords.Count > 0)
            {
                coord = live;
                return true;
            }
            if (run.VisitedMapCoords is { Count: > 0 } coords)
            {
                coord = coords[^1];
                return true;
            }
        }
        catch { /* 忽略 */ }
        return false;
    }

    /// <summary>
    /// 提取：地图生成类型 <paramref name="mapType"/>（分类 + 显示）、坐标 <paramref name="coord"/>。
    ///
    /// 关键根因（2026-08-10 反编译确认）：游戏 <c>EnterMapCoord</c> 先 <c>AddVisitedMapCoord</c>（坐标更新），
    /// 再 <c>EnterMapCoordInternal</c> → 先 <c>SaveRun</c>（触发检查点捕获）→ 后 <c>AppendToMapPointHistory</c>
    /// （历史条目才更新）。故在 SaveRun 瞬间：坐标已超前到新节点，但快照 <c>MapPointHistory</c> /
    /// <c>FloorReached</c> 仍停在上一节点 → 直接读快照字段必错位。
    ///
    /// 进一步发现（2026-08-10 实测）：SaveRun 瞬间 <c>CurrentMapPoint</c> 已随坐标更新到新节点，
    /// 但 <c>CurrentMapPointHistoryEntry</c> 仍停在旧节点。若用 HistoryEntry.Rooms[^1].RoomType 做显示，
    /// 会把新节点显示成旧节点的房间类型（如问号→商店仍显示战斗）。
    ///
    /// 修复：本方法只取【运行时坐标反查的地图生成类型】<c>state.CurrentMapPoint.PointType</c>
    /// （等价于 <c>Map.GetPoint(CurrentMapCoord)</c>）：这是地图在幕生成时即固定的类型，
    /// 由坐标唯一决定、在 SaveRun 瞬间已可用、绝不滞后。问号/先古等事件节点就用它显示为“事件/远古”，
    /// 不再依赖滞后的 Rooms。运行时不可用时回退快照字段。
    /// </summary>
    private static bool TryGetNodeFromHistory(SerializableRun run, out MapPointType mapType, out string coord)
    {
        mapType = default;
        coord = "";
        try
        {
            if (!GetLiveCoord(run, out var c)) return false;
            coord = $"坐标({c.col},{c.row})";

            // 权威地图类型：运行时当前节点 CurrentMapPoint.PointType（= Map.GetPoint(CurrentMapCoord)，
            // 由坐标决定、在 SaveRun 瞬间已可用、不滞后）。
            var rm = RunManager.Instance;
            var state = rm != null ? GetRunState(rm) : null;
            if (state?.CurrentMapPoint != null)
                mapType = state.CurrentMapPoint.PointType;
            else if (state != null)
            {
                var p = state.Map.GetPoint(c);
                if (p != null) mapType = p.PointType;
            }
            if (mapType == default)
            {
                var hist = run.MapPointHistory;
                if (run.CurrentActIndex >= 0 && run.CurrentActIndex < hist.Count)
                {
                    var e = hist[run.CurrentActIndex].LastOrDefault();
                    if (e != null) mapType = e.MapPointType;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前节点坐标键（用于同节点去重）。优先运行时权威坐标，无坐标时返回 null。</summary>
    private static string? CurrentNodeKey(SerializableRun run)
    {
        try
        {
            if (GetLiveCoord(run, out var c))
                return $"({c.col},{c.row})";
        }
        catch { /* 忽略 */ }
        return null;
    }

    /// <summary>
    /// 是否“只存进入检查点”的节点：商店、非战斗事件（含问号分支战斗）。
    /// 以地图生成类型 MapPointType 判定——问号分支成战斗仍保持 Unknown，故仍归入此类。
    /// 战斗类（Monster/Elite/Boss）保持原行为（进入+完成都存，使回退落在战后干净点）。
    /// 注：宝藏/休息点属“安全可重选”但不在用户本次范围，保持原行为（进入+完成都存）。
    /// </summary>
    private static bool IsOnlyEnterNode(MapPointType t) =>
        t == MapPointType.Shop || t == MapPointType.Unknown || t == MapPointType.Ancient;

    internal static string NodeTypeName(MapPointType t) => t switch
    {
        MapPointType.Monster => "战斗",
        MapPointType.Elite => "精英",
        MapPointType.RestSite => "休息",
        MapPointType.Shop => "商店",
        MapPointType.Treasure => "宝藏",
        MapPointType.Boss => "BOSS",
        MapPointType.Ancient => "远古",
        _ => "未知节点"
    };

    /// <summary>
    /// 地图生成类型 → 中文显示名。检查点捕获在 SaveRun 瞬间读取，此时 CurrentMapPointHistoryEntry
    /// 尚未更新，故不再依赖 Rooms/RoomType；统一用 CurrentMapPoint.PointType（= Map.GetPoint(coord)）。
    /// 问号(Unknown)显示为“事件”，先古(Ancient)显示为“远古”，其余按地图类型直接显示。
    /// </summary>
    internal static string DisplayNodeType(MapPointType mapType)
    {
        if (mapType == MapPointType.Ancient) return "远古";
        if (mapType == MapPointType.Unknown) return "事件";
        return NodeTypeName(mapType);
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

    /// <summary>RunManager.State 是 private RunState?，反射取出（与基游戏解耦，失败返回 null 不阻断）。</summary>
    private static RunState? GetRunState(RunManager rm)
    {
        try
        {
            var prop = typeof(RunManager).GetProperty("State", BindingFlags.NonPublic | BindingFlags.Instance);
            return prop?.GetValue(rm) as RunState;
        }
        catch
        {
            return null;
        }
    }

    // [v0.9.5-min] ManualCapture 随调试面板 CheckpointMarkerPad 一并移除（已无调用方）。
}
