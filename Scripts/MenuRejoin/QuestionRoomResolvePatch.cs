using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using AutoReconnect.Scripts.MenuRejoin;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.7.4 —— 修复「?」节点重连后房间类型错节（Task #120）。
///
/// ## 根因（已由 IL 反编译证实，见 _research/sts2_map/）
///
/// 游戏对 Unknown("?") 节点在“进入那一刻”用 run RNG 摇房间类型：
/// <c>RunManager.EnterMapPointInternal</c> → <c>RollRoomTypeFor</c> →
/// <c>State.Odds.UnknownMapPoint.Roll(blacklist, State)</c> 消耗 <c>State.Rng</c>，
/// 结果记进 <c>MapPointHistory</c>（随 <c>SerializableRun</c> 带回对局）。
///
/// 重连进入当前坐标走 <c>LoadIntoLatestMapCoord(preFinishedRoom = null)</c> →
/// <c>EnterMapCoordInternal</c> → <c>EnterMapPointInternal</c>，对 Unknown 节点**重摇**。
/// 此时掉线客机的 <c>State.Rng</c> 已与主机同步到“主机为那个 ? 消耗过一次摇”之后，
/// 于是摇出的是主机下一个 ? 的内容 → 房间类型错节（事件 vs 商店、旋螺 vs 活雾），
/// 静默累积直到最后房间加载失败 → 黑屏。
///
/// ## 修复
///
/// 重连进当前坐标前，从本机已反序列化的 <c>MapPointHistory</c> 取出该坐标
/// （必为 Unknown 节点）已解析的 <c>MapPointRoomHistoryEntry</c>（RoomType + ModelId），
/// 反射调用 <c>RunManager.CreateRoom</c> 构造对应 <c>AbstractRoom</c>，
/// 作为 <c>preFinishedRoom</c> 注入 <c>LoadIntoLatestMapCoord</c>。
///
/// <c>EnterMapPointInternal</c> 在 `pointType == Unknown &amp;&amp; preFinishedRoom != null` 时
/// 直接沿用 preFinishedRoom、不再重摇、也不二次 <c>AppendToMapPointHistory</c>。
///
/// ## 作用域
///
/// 仅对 <see cref="MenuRejoinFlow.IsRejoining"/>（菜单重连）生效；
/// <c>__0</c> 非 null（战斗 preFinishedRoom，由原生战斗重连路径处理）或当前节点非 Unknown 时跳过。
/// 普通对局流程完全不受影响。
/// </summary>
[HarmonyPatch(typeof(RunManager), "LoadIntoLatestMapCoord")]
internal static class QuestionRoomResolvePatch
{
    static void Prefix(RunManager __instance, ref AbstractRoom? __0)
    {
        // 只对菜单重连生效；且只在游戏本来要重摇（preFinishedRoom 为 null）时介入。
        if (!MenuRejoinFlow.IsRejoining) return;
        if (__0 != null) return;

        try
        {
            var resolved = ResolveCurrentRoom(__instance);
            if (resolved != null)
            {
                __0 = resolved;
                Diag.Log($"[MenuRejoin] 注入已解析的“?”房间（避免重摇）：{resolved.RoomType} / {resolved.ModelId}");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 解析当前“?”房间失败，回退原版重摇：{ex}");
        }
    }

    /// <summary>
    /// 从当前坐标的 MapPointHistory 记录构造已解析的房间；非 Unknown 节点或缺少记录则返回 null。
    /// </summary>
    static AbstractRoom? ResolveCurrentRoom(RunManager rm)
    {
        var state = GetState(rm);
        if (state == null || state.VisitedMapCoords.Count == 0) return null;

        var coord = state.VisitedMapCoords[state.VisitedMapCoords.Count - 1];
        var point = state.Map?.GetPoint(coord);
        // 只对 Unknown("?") 节点介入；固定类型节点由游戏确定性解析，无需干预。
        if (point == null || point.PointType != MapPointType.Unknown) return null;

        var historyEntry = state.CurrentMapPointHistoryEntry;
        if (historyEntry == null || historyEntry.Rooms.Count == 0) return null;

        var roomEntry = historyEntry.Rooms[0];
        var roomType = roomEntry.RoomType;

        AbstractModel? model = GetModelForRoom(roomEntry.ModelId, roomType);
        return InvokeCreateRoom(rm, roomType, point.PointType, model);
    }

    /// <summary>
    /// RunManager.State 是 private，跨程序集用反射读取。
    /// </summary>
    static RunState? GetState(RunManager rm)
    {
        var prop = typeof(RunManager).GetProperty("State", BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null) return null;
        try { return (RunState?)prop.GetValue(rm); }
        catch { return null; }
    }

    /// <summary>
    /// 按房间类型取对应的模型（事件→EventModel，战斗→EncounterModel，其余无需模型）。
    /// ModelId 是引用类型，用 == null 判断是否为空。
    /// </summary>
    static AbstractModel? GetModelForRoom(ModelId? modelId, RoomType roomType)
    {
        if (modelId == null) return null;
        try
        {
            if (roomType == RoomType.Event)
                return (AbstractModel?)ModelDb_GetById(typeof(EventModel), modelId);
            if (roomType == RoomType.Monster || roomType == RoomType.Elite || roomType == RoomType.Boss)
                return (AbstractModel?)ModelDb_GetById(typeof(EncounterModel), modelId);
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 获取房间模型失败（{roomType} / {modelId}）：{ex.Message}");
        }
        return null;
    }

    static object? ModelDb_GetById(Type modelType, ModelId id)
    {
        var method = typeof(ModelDb).GetMethod("GetById", BindingFlags.Public | BindingFlags.Static);
        if (method == null) return null;
        var generic = method.MakeGenericMethod(modelType);
        return generic.Invoke(null, new object[] { id });
    }

    /// <summary>
    /// RunManager.CreateRoom 是 private 实例方法，反射调用以复用游戏原生的房间构造逻辑。
    /// 传入已解析的 model 可避免 CreateRoom 内部再次 PullNext* 重新抽取内容。
    /// </summary>
    static AbstractRoom? InvokeCreateRoom(RunManager rm, RoomType roomType, MapPointType pointType, AbstractModel? model)
    {
        var method = typeof(RunManager).GetMethod("CreateRoom",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(RoomType), typeof(MapPointType), typeof(AbstractModel) },
            null);
        if (method == null) return null;
        try
        {
            return (AbstractRoom?)method.Invoke(rm, new object?[] { roomType, pointType, model });
        }
        catch (Exception ex)
        {
            Diag.Log($"[MenuRejoin] 反射调用 CreateRoom 失败（{roomType}）：{ex.Message}");
            return null;
        }
    }
}
