using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Watchdog;

public sealed class ProcessController(ILogger<ProcessController> logger)
{
    private readonly ILogger<ProcessController> _logger = logger;

    public bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Start(WatchdogTarget target)
    {
        var exePath = target.GetExePath();
        var workingDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        var args = target.Arguments ?? string.Empty;

        try
        {
            Directory.CreateDirectory(Path.Combine(workingDir, "watchdog"));
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            });
            _logger.LogInformation("Process started: {Name} ({Path})", target.Name, exePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start process {Name} at {Path}", target.Name, exePath);
        }
    }
}
