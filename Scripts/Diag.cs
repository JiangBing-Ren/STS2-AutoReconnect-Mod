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

            _logPath = Path.Combine(baseDir, "autoreconnectmin.log");
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
