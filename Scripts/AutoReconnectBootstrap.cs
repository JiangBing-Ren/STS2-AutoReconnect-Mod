using System.Runtime.CompilerServices;

namespace AutoReconnect.Scripts;

/// <summary>
/// CLR 加载本 DLL 时立即触发，早于任何其它代码。
/// 作为 [ModInitializer] 的兜底：即使加载器因故未调用 Entry.Init()，
/// 诊断日志与状态重置也已就绪（Entry.Init 自身幂等，重复调用安全）。
/// </summary>
internal static class AutoReconnectBootstrap
{
#pragma warning disable CA2255
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        Diag.Init();
        Diag.Log("--------------------------------------------");
        Diag.Log("AutoReconnect v0.9.6-min ModuleInitializer 触发");
        Diag.Log("--------------------------------------------");

        HostInfoTracker.Reset();
    }
}
