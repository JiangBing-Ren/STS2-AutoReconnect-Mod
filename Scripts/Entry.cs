using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace AutoReconnect.Scripts;

/// <summary>
/// v0.3.0 — 显式 mod 入口。
///
/// 已通过反编译 ModManager.TryLoadMod 确认游戏加载器行为：
///   Assembly.GetTypes() → 查找 [ModInitializer]
///     ├─ 找到 → CallModInitializer(type)（要求目标方法必须 static）
///     └─ 未找到 → Log("No ModInitializerAttribute detected. Calling Harmony.PatchAll for ...")
///                 → new Harmony(...).PatchAll(asm)
///
/// v0.2.0 走的是"自动 PatchAll"分支（补丁确实生效），但初始化顺序不可控。
/// v0.3.0 改为显式注册，确保 Diag / 状态重置在补丁挂载之前完成。
/// </summary>
[ModInitializer(nameof(Init))]
public class Entry
{
    private static Harmony? _harmony;
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized)
        {
            Diag.Log("Entry.Init() 重复调用，已忽略。");
            return;
        }
        _initialized = true;

        // 1. 诊断日志优先（补丁内部会用到）
        Diag.Init();
        Diag.Log("============================================");
        Diag.Log("AutoReconnect v0.7.1 Entry.Init() 开始");
        Diag.Log("============================================");

        // 2. 重置跟踪状态
        HostInfoTracker.Reset();
        Diag.Log("HostInfoTracker 已重置");

        // 2.5 向 ModConfig（若已安装）注册配置界面；未安装则自动跳过，不影响运行
        try
        {
            ModConfigBridge.DeferredRegister();
        }
        catch (Exception ex)
        {
            Diag.Log($"ModConfig 注册触发异常（已忽略）：{ex}");
        }

        // 3. 挂载 Harmony 补丁（客户端重连 + Host 端离线接管）
        //    逐类挂载而非 PatchAll：游戏更新导致某个目标方法消失时，
        //    只有该补丁失效，其余补丁照常工作（PatchAll 会因一个异常整体中断）。
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            _harmony = new Harmony("sts2.autoreconnect");

            int ok = 0, failed = 0, methods = 0;
            foreach (var type in asm.GetTypes())
            {
                if (type.GetCustomAttribute<HarmonyPatch>() == null) continue;

                try
                {
                    var patched = _harmony.CreateClassProcessor(type).Patch();
                    if (patched is { Count: > 0 })
                    {
                        ok++;
                        methods += patched.Count;
                    }
                    else
                    {
                        // Prepare() 返回 false 或无目标：属正常跳过
                        Diag.Log($"  跳过补丁类 {type.Name}（无生效目标）");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Diag.Log($"  补丁类 {type.Name} 挂载失败：{ex.InnerException?.Message ?? ex.Message}");
                }
            }

            Diag.Log($"Harmony 挂载完成：成功 {ok} 类 / 失败 {failed} 类 / 共 {methods} 个目标方法");
        }
        catch (Exception ex)
        {
            Diag.Log($"Harmony 挂载整体失败：{ex}");
        }

        Diag.Log($"离线接管：TakeoverEnabled={Ghost.OfflineTakeoverCore.TakeoverEnabled}（进入联机对局后自动激活）");
        Diag.Log("AutoReconnect v0.7.1 初始化完成。");
    }
}
