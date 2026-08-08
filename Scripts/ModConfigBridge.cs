// =============================================================================
// ModConfigBridge.cs — AutoReconnect 配置界面（基于 ModConfig-STS2 零依赖桥接）
// =============================================================================
// 复制自官方 ModConfigBridge 模板，按 AutoReconnect 定制。
// 通过反射调用 ModConfig，不引用其 DLL：
//   - 玩家安装 ModConfig 后，设置界面“Mods”标签页会出现本 mod 的配置项；
//   - 未安装 ModConfig 时，GetValue 返回 fallback，mod 照常工作（不强制依赖）。
// 官方仓库：https://github.com/xhyrzldf/ModConfig-STS2
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace AutoReconnect.Scripts;

/// <summary>
/// ModConfig-STS2 零依赖桥接。
/// </summary>
internal static class ModConfigBridge
{
    private static bool _available;
    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configTypeEnum;

    internal static bool IsAvailable => _available;

    // ─── Step 1: 在 Entry.Init() 中调用 ───────────────────────────
    // ModConfig 可能按字母序晚于本 mod 加载，故延迟到下一帧确保反射可用。

    internal static void DeferredRegister()
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.ProcessFrame += OnNextFrame;
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] ModConfig 延迟注册失败（将忽略配置界面）：{ex}");
        }
    }

    private static void OnNextFrame()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame -= OnNextFrame;
        Detect();
        if (_available) Register();
    }

    // ─── Step 2: 反射探测 ModConfig ───────────────────────────────

    private static void Detect()
    {
        try
        {
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                .ToArray();

            _apiType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
            _entryType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
            _configTypeEnum = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
            _available = _apiType != null && _entryType != null && _configTypeEnum != null;
        }
        catch
        {
            _available = false;
        }
    }

    // ─── Step 3: 注册配置项 ───────────────────────────────────────

    private static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            var entries = BuildEntries();

            var displayNames = new Dictionary<string, string>
            {
                ["en"] = "Auto Reconnect",
                ["zhs"] = "自动重连",
            };

            var registerMethod = _apiType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Register")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

            if (registerMethod.GetParameters().Length == 4)
            {
                registerMethod.Invoke(null, new object[] { "AutoReconnect", displayNames["en"], displayNames, entries });
            }
            else
            {
                registerMethod.Invoke(null, new object[] { "AutoReconnect", displayNames["en"], entries });
            }

            // 加载已保存的配置并应用到运行时
            ApplyPersisted();
            Diag.Log("[AutoReconnect] 已向 ModConfig 注册配置页（设置 → Mods）。");
        }
        catch (Exception e)
        {
            Diag.Log($"[AutoReconnect] ModConfig 注册失败：{e}");
        }
    }

    // ─── 读取 / 写入配置值 ────────────────────────────────────────

    /// <summary>读取已保存的配置值；ModConfig 未安装时返回 fallback。</summary>
    internal static T GetValue<T>(string key, T fallback)
    {
        if (!_available) return fallback;
        try
        {
            var result = _apiType!.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static)
                ?.MakeGenericMethod(typeof(T))
                ?.Invoke(null, new object[] { "AutoReconnect", key });
            return result != null ? (T)result : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>把值同步回 ModConfig（持久化）。</summary>
    internal static void SetValue(string key, object value)
    {
        if (!_available) return;
        try
        {
            _apiType!.GetMethod("SetValue", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new object[] { "AutoReconnect", key, value });
        }
        catch { }
    }

    /// <summary>把已保存的配置读到运行时静态字段（Register 时调用一次）。</summary>
    internal static void ApplyPersisted()
    {
        try
        {
            Ghost.OfflineTakeoverCore.TakeoverEnabled = GetValue("takeoverEnabled", true);
            Ghost.OfflineTakeoverCore.OfflineTakeoverDelayMs = (ulong)GetValue("takeoverDelayMs", 30000f);
            Ghost.HostZombieWatchdog.ZombieSilenceMs = (ulong)GetValue("zombieSilenceMs", 18000f);
            Ghost.HostZombieWatchdog.RejoinGraceMs = (ulong)GetValue("rejoinGraceMs", 30000f);
            Ghost.ReconnectBlockedPopup.ShowOnBlocked = GetValue("showRejoinBlockedPopup", true);
            ReconnectDiagnostics.ShowClientPopup = GetValue("showClientResultPopup", true);
            ReconnectDiagnostics.ShowHostPopup = GetValue("showHostEventPopup", true);
        }
        catch (Exception ex)
        {
            Diag.Log($"[AutoReconnect] 应用已保存配置失败：{ex}");
        }
    }

    // ═════════════════════════════════════════════════════════════
    //  配置项定义
    // ═════════════════════════════════════════════════════════════

    private static Array BuildEntries()
    {
        var list = new List<object>();

        // ─── 分区标题 ───────────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Label", "Offline Takeover");
            Set(cfg, "Labels", L("Offline Takeover", "离线接管"));
            Set(cfg, "Type", EnumVal("Header"));
        }));

        // ─── 接管总开关 ─────────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "takeoverEnabled");
            Set(cfg, "Label", "Enable offline takeover");
            Set(cfg, "Labels", L("Enable offline takeover", "启用离线接管"));
            Set(cfg, "Type", EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)true);
            Set(cfg, "Description", "Host auto-plays disconnected players so the run never freezes.");
            Set(cfg, "Descriptions", L("Host auto-plays disconnected players so the run never freezes.",
                "主机自动代打掉线玩家，对局不会卡住。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                Ghost.OfflineTakeoverCore.TakeoverEnabled = Convert.ToBoolean(v);
            }));
        }));

        // ─── 接管宽限时间 ───────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "takeoverDelayMs");
            Set(cfg, "Label", "Takeover grace period (ms)");
            Set(cfg, "Labels", L("Takeover grace period (ms)", "接管宽限时间（毫秒）"));
            Set(cfg, "Type", EnumVal("Slider"));
            Set(cfg, "DefaultValue", (object)30000f);
            Set(cfg, "Min", 2000f);
            Set(cfg, "Max", 60000f);
            Set(cfg, "Step", 1000f);
            Set(cfg, "Format", "F0");
            Set(cfg, "Description", "Wait this long after a disconnect before the host takes over the player.");
            Set(cfg, "Descriptions", L("Wait this long after a disconnect before the host takes over the player.",
                "掉线后等待这么久，主机才接管该玩家。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                Ghost.OfflineTakeoverCore.OfflineTakeoverDelayMs = (ulong)Convert.ToInt64(v);
            }));
        }));

        // ─── 僵尸沉默阈值 ───────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "zombieSilenceMs");
            Set(cfg, "Label", "Zombie silence threshold (ms)");
            Set(cfg, "Labels", L("Zombie silence threshold (ms)", "僵尸沉默阈值（毫秒）"));
            Set(cfg, "Type", EnumVal("Slider"));
            Set(cfg, "DefaultValue", (object)18000f);
            Set(cfg, "Min", 2000f);
            Set(cfg, "Max", 60000f);
            Set(cfg, "Step", 1000f);
            Set(cfg, "Format", "F0");
            Set(cfg, "Description", "Host treats a connected-but-silent client blocking the turn as a zombie after this silence, then takes it over.");
            Set(cfg, "Descriptions", L("Host treats a connected-but-silent client blocking the turn as a zombie after this silence, then takes it over.",
                "客机连着但卡住回合、沉默超过此时长时，主机判定为僵尸并托管（用于修复 Steam 静默重连导致的永久卡死）。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                Ghost.HostZombieWatchdog.ZombieSilenceMs = (ulong)Convert.ToInt64(v);
            }));
        }));

        // ─── 重连握手宽限期 ─────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "rejoinGraceMs");
            Set(cfg, "Label", "Rejoin handshake grace (ms)");
            Set(cfg, "Labels", L("Rejoin handshake grace (ms)", "重连握手宽限期（毫秒）"));
            Set(cfg, "Type", EnumVal("Slider"));
            Set(cfg, "DefaultValue", (object)30000f);
            Set(cfg, "Min", 5000f);
            Set(cfg, "Max", 60000f);
            Set(cfg, "Step", 1000f);
            Set(cfg, "Format", "F0");
            Set(cfg, "Description", "After a client's transport reconnects, the host waits this long for the rejoin handshake before treating it as a silent zombie. Too small will kick legitimate reconnects (error 1016).");
            Set(cfg, "Descriptions", L("After a client's transport reconnects, the host waits this long for the rejoin handshake before treating it as a silent zombie. Too small will kick legitimate reconnects (error 1016).",
                "客机网络连上后，主机等待重连握手的时长；超时才判为静默僵尸并强断。设得太小会误踢正常重连（错误码 1016）。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                Ghost.HostZombieWatchdog.RejoinGraceMs = (ulong)Convert.ToInt64(v);
            }));
        }));

        // ─── 分隔线 ─────────────────────────────────────────────
        list.Add(Entry(cfg => Set(cfg, "Type", EnumVal("Separator"))));

        // ─── 诊断弹窗分区 ───────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Type", EnumVal("Header"));
            Set(cfg, "Label", "Reconnect Diagnostics");
            Set(cfg, "Labels", L("Reconnect Diagnostics", "重连诊断弹窗"));
        }));

        // ─── 客机结果弹窗 ───────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "showClientResultPopup");
            Set(cfg, "Label", "Show reconnect result (client)");
            Set(cfg, "Labels", L("Show reconnect result (client)", "客机显示重连结果弹窗"));
            Set(cfg, "Type", EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)true);
            Set(cfg, "Description", "Always show a popup after a reconnect attempt, success or failure, including which step failed and why.");
            Set(cfg, "Descriptions", L("Always show a popup after a reconnect attempt, success or failure, including which step failed and why.",
                "每次重连结束都弹窗告知结果；失败时显示卡在哪一步、具体原因和建议。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                ReconnectDiagnostics.ShowClientPopup = Convert.ToBoolean(v);
            }));
        }));

        // ─── 主机事件弹窗 ───────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "showHostEventPopup");
            Set(cfg, "Label", "Show teammate reconnect events (host)");
            Set(cfg, "Labels", L("Show teammate reconnect events (host)", "主机显示队友重连事件弹窗"));
            Set(cfg, "Type", EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)true);
            Set(cfg, "Description", "Host-side popups when a teammate disconnects, reconnects, is rejected, or has their zombie connection reset.");
            Set(cfg, "Descriptions", L("Host-side popups when a teammate disconnects, reconnects, is rejected, or has their zombie connection reset.",
                "队友掉线、重连成功、重连被拒、假死连接被重置时，房主也能看到弹窗与原因。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                ReconnectDiagnostics.ShowHostPopup = Convert.ToBoolean(v);
            }));
        }));

        // ─── 重连拦截提示开关 ───────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "showRejoinBlockedPopup");
            Set(cfg, "Label", "Show 'wait for battle' popup");
            Set(cfg, "Labels", L("Show 'wait for battle' popup", "显示“等战斗结束再重连”提示"));
            Set(cfg, "Type", EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)true);
            Set(cfg, "Description", "When a rejoin is blocked (battle in progress), show a popup telling the player to wait.");
            Set(cfg, "Descriptions", L("When a rejoin is blocked (battle in progress), show a popup telling the player to wait.",
                "重连被拦截（战斗进行中）时，弹出提示让玩家等待。"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                Ghost.ReconnectBlockedPopup.ShowOnBlocked = Convert.ToBoolean(v);
            }));
        }));

        // ─── 邀请分区 ───────────────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Type", EnumVal("Header"));
            Set(cfg, "Label", "Invite / Reconnect");
            Set(cfg, "Labels", L("Invite / Reconnect", "邀请 / 重连"));
        }));

        // ─── 邀请掉线玩家按钮 ───────────────────────────────────
        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "inviteDisconnected");
            Set(cfg, "Label", "Invite disconnected player");
            Set(cfg, "Labels", L("Invite disconnected player", "邀请掉线玩家"));
            Set(cfg, "Type", EnumVal("Button"));
            Set(cfg, "ButtonText", "Invite");
            Set(cfg, "ButtonTexts", L("Invite", "邀请"));
            Set(cfg, "Description", "Host-only. Opens the Steam invite dialog so you can pull a disconnected player back into the current run (the in-lobby invite button disappears once the run starts).");
            Set(cfg, "Descriptions", L("Host-only. Opens the Steam invite dialog so you can pull a disconnected player back into the current run (the in-lobby invite button disappears once the run starts).",
                "仅房主可用。打开 Steam 邀请对话框，把掉线玩家重新邀请回本局（开局前的邀请按钮在对局开始后就消失了）。"));
            Set(cfg, "OnChanged", new Action<object>(_ => Ghost.InviteHelper.TryInvite()));
        }));

        var result = Array.CreateInstance(_entryType!, list.Count);
        for (int i = 0; i < list.Count; i++)
            result.SetValue(list[i], i);
        return result;
    }

    // ═════════════════════════════════════════════════════════════
    //  反射辅助（无需修改）
    // ═════════════════════════════════════════════════════════════

    private static object Entry(Action<object> configure)
    {
        var inst = Activator.CreateInstance(_entryType!)!;
        configure(inst);
        return inst;
    }

    private static void Set(object obj, string name, object value)
        => obj.GetType().GetProperty(name)?.SetValue(obj, value);

    private static Dictionary<string, string> L(string en, string zhs)
        => new() { ["en"] = en, ["zhs"] = zhs };

    private static object EnumVal(string name)
        => Enum.Parse(_configTypeEnum!, name);
}
