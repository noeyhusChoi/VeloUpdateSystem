using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Watchdog;

public sealed class WatchdogService : BackgroundService
{
    private readonly WatchdogOptions _options;
    private readonly Dictionary<string, RestartLimiter> _restartLimiters = new();
    private readonly ILogger<WatchdogService> _logger;

    public WatchdogService(
        IOptions<WatchdogOptions> options,
        ILogger<WatchdogService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var target in _options.Targets)
        {
            _logger.LogInformation(
                "Watchdog target registered: Name={Name} ExePath={ExePath}",
                target.Name,
                target.GetExePath());
        }

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var target in _options.Targets)
            {
                await CheckTargetAsync(target).ConfigureAwait(false);
            }
        }
    }

    private async Task CheckTargetAsync(WatchdogTarget target)
    {
        var processName = target.Name;
        var updateLockPath = target.GetUpdateLockPath();
        var heartbeatPath = target.GetHeartbeatPath();
        var updateSuppressed = IsUpdateSuppressed(updateLockPath, out var suppressionReason);

        if (!updateSuppressed && !IsProcessRunning(processName))
        {
            await RestartProcessAsync(target, "crash", minDelaySec: 0).ConfigureAwait(false);
        }

        var heartbeat = ReadHeartbeat(heartbeatPath);
        if (heartbeat is not null)
        {
            var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
            if (!updateSuppressed && DateTimeOffset.UtcNow - heartbeat.TimestampUtc > timeout)
            {
                await RestartProcessAsync(target, "heartbeatTimeout", minDelaySec: 0).ConfigureAwait(false);
            }
        }

        var heartbeatAgeSec = heartbeat is null
            ? (double?)null
            : (DateTimeOffset.UtcNow - heartbeat.TimestampUtc).TotalSeconds;
        _logger.LogInformation(
            "Watchdog tick: target={Target} running={Running} heartbeatAgeSec={HeartbeatAgeSec} suppressed={Suppressed} reason={Reason}",
            processName,
            IsProcessRunning(processName),
            heartbeatAgeSec,
            updateSuppressed,
            suppressionReason ?? "none");
    }

    private static bool IsProcessRunning(string processName)
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

    private async Task RestartProcessAsync(WatchdogTarget target, string reason, int minDelaySec)
    {
        var processName = target.Name;
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(_options.RestartWindowMinutes);

        var limiter = GetLimiter(processName);
        if (!limiter.TryRegisterRestart(now, window, _options.MaxRestartsInWindow))
        {
            _logger.LogWarning("Restart backoff active for {ProcessName}.", processName);
            await Task.Delay(TimeSpan.FromMinutes(_options.BackoffMinutes)).ConfigureAwait(false);
            return;
        }

        if (minDelaySec > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(minDelaySec)).ConfigureAwait(false);
        }

        _logger.LogWarning("Restarting {ProcessName} due to {Reason}.", processName, reason);
        StartProcess(target);
    }

    private void StartProcess(WatchdogTarget target)
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

    private RestartLimiter GetLimiter(string name)
    {
        if (!_restartLimiters.TryGetValue(name, out var limiter))
        {
            limiter = new RestartLimiter();
            _restartLimiters[name] = limiter;
        }

        return limiter;
    }

    private bool IsUpdateSuppressed(string updateLockPath, out string? reason)
    {
        reason = null;
        if (!File.Exists(updateLockPath))
        {
            return false;
        }

        try
        {
            var payload = File.ReadAllText(updateLockPath);
            var info = JsonSerializer.Deserialize<UpdateLockInfo>(payload, JsonOptions);
            if (info is null)
            {
                return true;
            }

            reason = info.Reason;
            if (info.ExpiresAtUtc == DateTimeOffset.MinValue)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow <= info.ExpiresAtUtc)
            {
                return true;
            }

            File.Delete(updateLockPath);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static HeartbeatInfo? ReadHeartbeat(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var payload = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HeartbeatInfo>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record HeartbeatInfo(DateTimeOffset TimestampUtc, int Pid, bool Responsive, int IdleSeconds);
    private sealed record UpdateLockInfo(DateTimeOffset ExpiresAtUtc, string? Reason);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
