using System;
using System.IO;

namespace AutoReconnect.Scripts;

/// <summary>
/// File-based diagnostic logger, self-initializing via ModuleInitializer.
/// Logs to: %APPDATA%/SlayTheSpire2/mods/AutoReconnectMin/logs/autoreconnectmin.log
/// (matching the CDC convention)
/// </summary>
internal static class Diag
{
    private static string? _logPath;
    private static readonly object _lock = new();

    public static void Init()
    {
        if (_logPath != null) return;

        try
        {
            // Path convention matching CardDisableControl:
            // %APPDATA%/Roaming/SlayTheSpire2/mods/{ModName}/logs/{file}.log
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "mods", "AutoReconnectMin", "logs");

            Directory.CreateDirectory(baseDir);

            var currentPath = Path.Combine(baseDir, "autoreconnectmin.log");

            // 滚动归档（与官方 godot.log 行为一致）：每次新启动，先把上一会话的
            // autoreconnectmin.log 重命名为带时间戳的文件，历史全部保留、绝不覆盖。
            // 时间戳取上一会话日志的最后写入时间（即该会话结束时刻），格式 yyyy-MM-ddTHH.mm.ss
            // （Windows 文件名禁用冒号，时间用点分隔，对齐官方 godot<timestamp>.log）。
            if (File.Exists(currentPath))
            {
                try
                {
                    var stamp = File.GetLastWriteTime(currentPath)
                        .ToString("yyyy-MM-ddTHH.mm.ss");
                    var archived = Path.Combine(baseDir, $"autoreconnectmin{stamp}.log");
                    if (File.Exists(archived))
                        archived = Path.Combine(baseDir,
                            $"autoreconnectmin{stamp}_{Guid.NewGuid():N}.log");
                    File.Move(currentPath, archived);
                }
                catch (Exception ex)
                {
                    Log($"日志滚动归档失败（忽略，继续写当前文件）：{ex.Message}");
                }
            }

            _logPath = currentPath;
            Log("Diag init OK");
        }
        catch (Exception ex)
        {
            // Last resort: try writing next to the game exe
            try
            {
                var fallback = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "autoreconnect.log");
                _logPath = fallback;
                Log($"Diag init fallback: {fallback} (primary failed: {ex.Message})");
            }
            catch
            {
                _logPath = null;
            }
        }
    }

    public static void Log(string message)
    {
        try
        {
            if (_logPath == null)
            {
                Init();
                if (_logPath == null) return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            lock (_lock)
            {
                File.AppendAllText(_logPath, $"[{timestamp}] {message}\n");
            }
        }
        catch { }
    }
}
