using System;
using System.IO;

namespace Watchdog;

public sealed class WatchdogTarget
{
    public string Name { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string? PackId { get; set; }
    public string? ExeName { get; set; }
    public string? Arguments { get; set; }
    public string? HeartbeatFile { get; set; }
    public string? UpdateLockFile { get; set; }

    public string GetExePath()
    {
        if (string.IsNullOrWhiteSpace(ExePath) && !string.IsNullOrWhiteSpace(PackId))
        {
            var localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                PackId);
            var exeName = string.IsNullOrWhiteSpace(ExeName) ? $"{PackId}.exe" : ExeName;
            return Path.Combine(localRoot, "current", exeName);
        }

        if (!string.IsNullOrWhiteSpace(ExePath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(ExePath);
            if (Path.IsPathRooted(expanded))
            {
                return expanded;
            }

            return Path.Combine(AppContext.BaseDirectory, expanded);
        }

        if (Path.IsPathRooted(ExePath))
        {
            return ExePath;
        }

        return Path.Combine(AppContext.BaseDirectory, ExePath);
    }

    public string GetHeartbeatPath()
    {
        if (!string.IsNullOrWhiteSpace(HeartbeatFile))
        {
            var expanded = Environment.ExpandEnvironmentVariables(HeartbeatFile);
            return Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(AppContext.BaseDirectory, expanded);
        }

        var exeDir = Path.GetDirectoryName(GetExePath()) ?? AppContext.BaseDirectory;
        var watchdogDir = Path.Combine(exeDir, "watchdog");
        return Path.Combine(watchdogDir, "heartbeat.json");
    }

    public string GetUpdateLockPath()
    {
        if (!string.IsNullOrWhiteSpace(UpdateLockFile))
        {
            var expanded = Environment.ExpandEnvironmentVariables(UpdateLockFile);
            return Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(AppContext.BaseDirectory, expanded);
        }

        var exeDir = Path.GetDirectoryName(GetExePath()) ?? AppContext.BaseDirectory;
        var watchdogDir = Path.Combine(exeDir, "watchdog");
        return Path.Combine(watchdogDir, "update.lock.json");
    }
}
