using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace AutoReconnect.Scripts.Reload;

// ============================================================================
// RejoinTransitionGuard
// ============================================================================

/// <summary>
/// 重载期间的过渡守卫：在原地重载的 LoadRun 阶段跳过 NTransition 的淡入淡出动画，
/// 避免 LoadRun 内部触发的 RoomFade 等二次过渡与外部 FadeOut/FadeIn 叠加造成视觉闪烁。
///
/// 使用方式：在 FadeOut 之后、FadeIn 之前用 using 包住重载操作：
/// <code>
/// await game.Transition.FadeOut();
/// using (RejoinTransitionGuard.SuppressTransitions())
/// {
///     // LoadRun 等操作——内部触发的过渡被跳过
/// }
/// await game.Transition.FadeIn();
/// </code>
///
/// 参考 QuickSL 的 QuickSlTransitionGuard，适配为不依赖 JML 的独立实现。
/// </summary>
internal static class RejoinTransitionGuard
{
    private static int _suppressDepth;

    /// <summary>激活过渡抑制。返回的 IDisposable 释放时恢复。深度计数器支持嵌套。</summary>
    public static IDisposable SuppressTransitions()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new TransitionSuppression();
    }

    /// <summary>
    /// Harmony Prefix 用：若过渡抑制激活，将 __result 替换为 Task.CompletedTask 并返回 false（跳过原方法）。
    /// </summary>
    public static bool TrySkipTransition(string transitionName, ref Task result)
    {
        if (Volatile.Read(ref _suppressDepth) <= 0)
            return true; // 不抑制，执行原方法

        Diag.Log($"[Reload] 过渡抑制激活，跳过{transitionName}动画。");
        result = Task.CompletedTask;
        return false;
    }

    private sealed class TransitionSuppression : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Decrement(ref _suppressDepth);
        }
    }
}

// ── Harmony 补丁：NTransition 四个过渡方法 ──

[HarmonyPatch(typeof(NTransition), nameof(NTransition.FadeOut))]
internal static class Patch_NTransition_FadeOut
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
        => RejoinTransitionGuard.TrySkipTransition("屏幕淡出", ref __result);
}

[HarmonyPatch(typeof(NTransition), nameof(NTransition.FadeIn))]
internal static class Patch_NTransition_FadeIn
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
        => RejoinTransitionGuard.TrySkipTransition("屏幕淡入", ref __result);
}

[HarmonyPatch(typeof(NTransition), nameof(NTransition.RoomFadeOut))]
internal static class Patch_NTransition_RoomFadeOut
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
        => RejoinTransitionGuard.TrySkipTransition("房间淡出", ref __result);
}

[HarmonyPatch(typeof(NTransition), nameof(NTransition.RoomFadeIn))]
internal static class Patch_NTransition_RoomFadeIn
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
        => RejoinTransitionGuard.TrySkipTransition("房间淡入", ref __result);
}

// ============================================================================
// RejoinSceneReloadGuard
// ============================================================================

/// <summary>
/// 重载期间的场景守卫：
/// 1. 在原地重载时保持 TopBar 层数/房间图标不闪烁（快照 + NTopBar.Initialize Postfix 恢复）；
/// 2. 抑制旧手牌失焦时的布局刷新异常（NPlayerHand.OnHolderUnfocused Prefix 跳过）；
/// 3. 切换场景前释放旧手牌焦点（PrepareCurrentHandForSceneSwap）。
///
/// 参考 QuickSL 的 QuickSlSceneReloadGuard，适配为不依赖 JML 的独立实现：
/// 用 System.Reflection.FieldInfo 替代 JML 的 MemberAccessor。
/// </summary>
internal static class RejoinSceneReloadGuard
{
    private static int _suppressLateHandLayoutDepth;
    private static int _preserveTopBarDepth;
    private static TopBarSnapshot? _topBarSnapshot;
    private static readonly object _snapshotLock = new();

    // Lazy-init field accessors（替代 JML MemberAccessor）
    private static FieldAccessor? _floorNumLabelAccessor;
    private static FieldAccessor? _roomIconAccessor;
    private static FieldAccessor? _roomIconOutlineAccessor;
    private static bool _accessorsInit;

    private static void EnsureAccessors()
    {
        if (_accessorsInit) return;
        _accessorsInit = true;
        try
        {
            _floorNumLabelAccessor = FieldAccessor.Create(typeof(NTopBarFloorIcon), "_floorNumLabel");
            _roomIconAccessor = FieldAccessor.Create(typeof(NTopBarRoomIcon), "_roomIcon");
            _roomIconOutlineAccessor = FieldAccessor.Create(typeof(NTopBarRoomIcon), "_roomIconOutline");

            if (_floorNumLabelAccessor == null || _roomIconAccessor == null || _roomIconOutlineAccessor == null)
                Diag.Log("[Reload] TopBar 内部字段反射不完整，层数/房间图标快照修复将部分降级。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Reload] TopBar 内部字段反射失败，禁用层数/房间图标快照修复：{ex.Message}");
        }
    }

    // ── TopBar 快照 ──

    /// <summary>
    /// 缓存当前 TopBar 的层数文字与房间图标状态。
    /// 在 LoadRun 之前调用；新场景的 NTopBar.Initialize 触发时自动恢复。
    /// </summary>
    public static IDisposable PreserveStableTopBarLocation()
    {
        EnsureAccessors();
        var snapshot = TopBarSnapshot.Capture();
        if (snapshot == null)
            return NoopDisposable.Instance;

        lock (_snapshotLock)
        {
            _topBarSnapshot = snapshot;
            _preserveTopBarDepth++;
        }

        Diag.Log("[Reload] 已缓存旧 TopBar 的层数与房间图标显示。");
        return new TopBarPreservation();
    }

    /// <summary>NTopBar.Initialize Postfix 入口：若处于保持周期且尚无当前房间，恢复快照。</summary>
    public static void RestoreStableTopBarLocationIfNeeded(NTopBar topBar, IRunState runState)
    {
        // 仅在初始载入（无当前房间）时恢复；正常游戏中 CurrentRoom 非空，不动 TopBar。
        if (runState.CurrentRoom != null) return;

        TopBarSnapshot? snapshot;
        lock (_snapshotLock)
        {
            if (_preserveTopBarDepth <= 0) return;
            snapshot = _topBarSnapshot;
        }

        if (snapshot == null) return;

        try
        {
            snapshot.Apply(topBar, runState);
            Diag.Log("[Reload] 新 TopBar 初始化时已沿用旧层数与房间图标显示。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Reload] 恢复 TopBar 层数/房间图标失败：{ex.Message}");
        }
    }

    // ── 旧手牌失焦抑制 ──

    /// <summary>
    /// 激活「旧手牌失焦抑制」：场景切换期间旧手牌节点已离开场景树，
    /// OnHolderUnfocused 回调会引发布局刷新异常。抑制期间跳过这些回调。
    /// </summary>
    public static IDisposable SuppressLateHandLayoutRefresh()
    {
        Interlocked.Increment(ref _suppressLateHandLayoutDepth);
        return new LateHandLayoutSuppression();
    }

    /// <summary>NPlayerHand.OnHolderUnfocused Prefix 入口：判断是否应跳过本次失焦回调。</summary>
    public static bool ShouldSkipLateHandUnfocus(NPlayerHand hand, NHandCardHolder? holder)
    {
        if (Volatile.Read(ref _suppressLateHandLayoutDepth) <= 0)
            return false; // 未激活抑制，不跳过

        // 手牌节点已离开场景树 → 跳过
        if (!GodotObject.IsInstanceValid(hand) || !hand.IsInsideTree())
            return true;

        // CardHolderContainer 已失效 → 跳过
        if (hand.CardHolderContainer is not { } container ||
            !GodotObject.IsInstanceValid(container) ||
            !container.IsInsideTree())
            return true;

        // holder 已失效 → 跳过
        return holder == null || !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree();
    }

    // ── 切换场景前手牌预处理 ──

    /// <summary>
    /// 在 LoadRun 之前释放当前手牌所有卡牌的焦点与点击启用状态，
    /// 避免场景切换时残留焦点导致布局刷新异常。
    /// </summary>
    public static void PrepareCurrentHandForSceneSwap()
    {
        try
        {
            if (NPlayerHand.Instance is not { } hand ||
                !GodotObject.IsInstanceValid(hand) ||
                !hand.IsInsideTree())
                return;

            int prepared = 0;
            foreach (NHandCardHolder holder in hand.ActiveHolders)
            {
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
                    continue;

                holder.ReleaseFocus();
                var hitbox = holder.Hitbox;
                if (!GodotObject.IsInstanceValid(hitbox))
                    continue;

                hitbox.ReleaseFocus();
                if (hitbox.IsEnabled)
                {
                    hitbox.SetEnabled(false);
                    prepared++;
                }
            }

            if (prepared > 0)
                Diag.Log($"[Reload] 切换场景前已释放 {prepared} 张旧手牌的焦点。");
        }
        catch (Exception ex)
        {
            Diag.Log($"[Reload] 预处理旧手牌焦点失败：{ex.Message}");
        }
    }

    // ── 嵌套类型 ──

    private sealed class TopBarPreservation : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_snapshotLock)
            {
                _preserveTopBarDepth--;
                if (_preserveTopBarDepth <= 0)
                {
                    _preserveTopBarDepth = 0;
                    _topBarSnapshot = null;
                }
            }
        }
    }

    private sealed class LateHandLayoutSuppression : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Decrement(ref _suppressLateHandLayoutDepth);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        private NoopDisposable() { }
        public void Dispose() { }
    }

    /// <summary>简单的私有字段反射访问器（替代 JML MemberAccessor）。</summary>
    private sealed class FieldAccessor
    {
        private readonly FieldInfo _field;
        private FieldAccessor(FieldInfo field) => _field = field;

        public static FieldAccessor? Create(Type type, string fieldName)
        {
            var f = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return f != null ? new FieldAccessor(f) : null;
        }

        public object? GetValue(object obj) => _field.GetValue(obj);
    }

    /// <summary>TopBar 层数文字与房间图标的状态快照。</summary>
    private sealed class TopBarSnapshot
    {
        private readonly string? _floorText;
        private readonly Texture2D? _roomIconTexture;
        private readonly bool _roomIconVisible;
        private readonly Texture2D? _roomIconOutlineTexture;
        private readonly bool _roomIconOutlineVisible;
        private readonly Control.FocusModeEnum _roomIconFocusMode;
        private readonly Control.MouseFilterEnum _roomIconMouseFilter;

        private TopBarSnapshot(
            string? floorText,
            Texture2D? roomIconTexture, bool roomIconVisible,
            Texture2D? roomIconOutlineTexture, bool roomIconOutlineVisible,
            Control.FocusModeEnum roomIconFocusMode,
            Control.MouseFilterEnum roomIconMouseFilter)
        {
            _floorText = floorText;
            _roomIconTexture = roomIconTexture;
            _roomIconVisible = roomIconVisible;
            _roomIconOutlineTexture = roomIconOutlineTexture;
            _roomIconOutlineVisible = roomIconOutlineVisible;
            _roomIconFocusMode = roomIconFocusMode;
            _roomIconMouseFilter = roomIconMouseFilter;
        }

        public static TopBarSnapshot? Capture()
        {
            try
            {
                EnsureAccessors();
                if (_floorNumLabelAccessor == null &&
                    _roomIconAccessor == null &&
                    _roomIconOutlineAccessor == null)
                    return null;

                var topBar = NRun.Instance?.GlobalUi?.TopBar;
                if (topBar == null || !GodotObject.IsInstanceValid(topBar))
                    return null;

                MegaLabel? floorLabel = GetFloorLabel(topBar.FloorIcon);
                TextureRect? roomIcon = GetRoomIcon(topBar.RoomIcon);
                TextureRect? roomIconOutline = GetRoomIconOutline(topBar.RoomIcon);

                if (floorLabel == null && roomIcon == null && roomIconOutline == null)
                    return null;

                return new TopBarSnapshot(
                    floorLabel?.Text,
                    roomIcon?.Texture,
                    roomIcon?.Visible ?? false,
                    roomIconOutline?.Texture,
                    roomIconOutline?.Visible ?? false,
                    topBar.RoomIcon.FocusMode,
                    topBar.RoomIcon.MouseFilter);
            }
            catch (Exception ex)
            {
                Diag.Log($"[Reload] 读取旧 TopBar 层数/房间图标失败：{ex.Message}");
                return null;
            }
        }

        public void Apply(NTopBar topBar, IRunState runState)
        {
            if (!GodotObject.IsInstanceValid(topBar))
                return;

            MegaLabel? floorLabel = GetFloorLabel(topBar.FloorIcon);
            if (floorLabel != null)
            {
                string floorText = !string.IsNullOrWhiteSpace(_floorText)
                    ? _floorText
                    : runState.TotalFloor.ToString();
                floorLabel.SetTextAutoSize(floorText);
            }

            TextureRect? roomIcon = GetRoomIcon(topBar.RoomIcon);
            if (roomIcon != null)
            {
                roomIcon.Texture = _roomIconTexture;
                roomIcon.Visible = _roomIconVisible;
            }

            TextureRect? roomIconOutline = GetRoomIconOutline(topBar.RoomIcon);
            if (roomIconOutline != null)
            {
                roomIconOutline.Texture = _roomIconOutlineTexture;
                roomIconOutline.Visible = _roomIconOutlineVisible;
            }

            topBar.RoomIcon.FocusMode = _roomIconFocusMode;
            topBar.RoomIcon.MouseFilter = _roomIconMouseFilter;
        }

        private static MegaLabel? GetFloorLabel(NTopBarFloorIcon? floorIcon)
        {
            if (floorIcon == null || !GodotObject.IsInstanceValid(floorIcon) ||
                _floorNumLabelAccessor == null)
                return null;
            return _floorNumLabelAccessor.GetValue(floorIcon) as MegaLabel;
        }

        private static TextureRect? GetRoomIcon(NTopBarRoomIcon? roomIcon)
        {
            if (roomIcon == null || !GodotObject.IsInstanceValid(roomIcon) ||
                _roomIconAccessor == null)
                return null;
            return _roomIconAccessor.GetValue(roomIcon) as TextureRect;
        }

        private static TextureRect? GetRoomIconOutline(NTopBarRoomIcon? roomIcon)
        {
            if (roomIcon == null || !GodotObject.IsInstanceValid(roomIcon) ||
                _roomIconOutlineAccessor == null)
                return null;
            return _roomIconOutlineAccessor.GetValue(roomIcon) as TextureRect;
        }
    }
}

// ── Harmony 补丁：场景重载守卫 ──

[HarmonyPatch(typeof(NPlayerHand), "OnHolderUnfocused")]
internal static class Patch_NPlayerHand_OnHolderUnfocused
{
    [HarmonyPrefix]
    private static bool Prefix(NPlayerHand __instance, NHandCardHolder holder)
    {
        if (!RejoinSceneReloadGuard.ShouldSkipLateHandUnfocus(__instance, holder))
            return true; // 不跳过，执行原方法

        Diag.Log("[Reload] 旧手牌已离开场景树，跳过失焦布局刷新。");
        return false; // 跳过原方法
    }
}

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
internal static class Patch_NTopBar_Initialize
{
    [HarmonyPostfix]
    private static void Postfix(NTopBar __instance, IRunState runState)
    {
        RejoinSceneReloadGuard.RestoreStableTopBarLocationIfNeeded(__instance, runState);
    }
}
