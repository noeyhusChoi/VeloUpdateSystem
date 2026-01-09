using Microsoft.Extensions.Options;

namespace Watchdog;

public sealed class WatchdogService(
    IOptions<WatchdogOptions> options,
    RestartPolicy restartPolicy,
    ProcessController processController,
    ILogger<WatchdogService> logger)
    : BackgroundService
{
    private readonly WatchdogOptions _options = options.Value;
    private readonly RestartPolicy _restartPolicy = restartPolicy;
    private readonly ProcessController _processController = processController;
    private readonly ILogger<WatchdogService> _logger = logger;
    private readonly Dictionary<string, DateTimeOffset> _cooldownStartUtc = [];


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Log resolved targets once on startup.
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
        // Guard restart behavior based on lock/heartbeat files and backoff policy.

        // Check Process
        var processName = target.GetProcessName();
        var isProcessRunning = _processController.IsRunning(processName);

        // Check Update
        var updateLockPath = target.GetUpdateLockPath();
        var updateLock = UpdateLockFileStore.Read(updateLockPath);
        var isUpdateLocked = IsUpdateLocked(updateLock, updateLockPath);

        // Check Heartbeat
        var heartbeatPath = target.GetHeartbeatPath();
        var heartbeat = HeartbeatFileStore.Read(heartbeatPath);
        var isHeartbeatStale = IsHeartbeatStale(heartbeat);


        // Restart logic
        // 1. Update lock active -> no action
        if (isUpdateLocked)
        {
            LogTargetStatus(processName, isProcessRunning, heartbeat, isHeartbeatStale, updateLock, isUpdateLocked);
            return;
        }

        if (IsCooldownActive(processName))
        {
            LogTargetStatus(processName, isProcessRunning, heartbeat, isHeartbeatStale, updateLock, isUpdateLocked);
            return;
        }

        // 2. Not running -> restart (crash)
        if (!isProcessRunning)
        {
            await RestartProcessAsync(target, "crash", minDelaySec: 0).ConfigureAwait(false);
            LogTargetStatus(processName, isProcessRunning, heartbeat, isHeartbeatStale, updateLock, isUpdateLocked);
            return;
        }

        // 3. Running but heartbeat problem -> restart (hang)
        if (isHeartbeatStale)
        {

            await RestartProcessAsync(target, "heartbeatTimeout", minDelaySec: 0).ConfigureAwait(false);
            LogTargetStatus(processName, isProcessRunning, heartbeat, isHeartbeatStale, updateLock, isUpdateLocked);
            return;
        }

        StopCooldown(processName);
        LogTargetStatus(processName, isProcessRunning, heartbeat, isHeartbeatStale, updateLock, isUpdateLocked);
    }

    private async Task RestartProcessAsync(WatchdogTarget target, string reason, int minDelaySec)
    {
        var processName = target.GetProcessName();
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(_options.RestartWindowMinutes);

        if (!_restartPolicy.CanRestart(processName, now, window, _options.MaxRestartsInWindow))
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
        _processController.Start(target);
        StartCooldown(processName);
    }

    private static bool IsUpdateLocked(UpdateLockInfo? info, string lockPath)
    {
        if (info is null)
        {
            return false;
        }

        if (info.ExpiresAtUtc == DateTimeOffset.MinValue)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow <= info.ExpiresAtUtc)
        {
            return true;
        }

        UpdateLockFileStore.Delete(lockPath);
        return false;
    }

    private bool IsHeartbeatStale(HeartbeatInfo? heartbeat)
    {
        if (heartbeat is null)
        {
            return false;
        }

        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        return DateTimeOffset.UtcNow - heartbeat.TimestampUtc > timeout;
    }

    private bool IsCooldownActive(string processName)
    {
        if (!_cooldownStartUtc.TryGetValue(processName, out var lastStart))
        {
            return false;
        }

        var cooldown = TimeSpan.FromSeconds(_options.StartGraceSeconds);
        return DateTimeOffset.UtcNow - lastStart < cooldown;
    }

    private void StartCooldown(string processName)
    {
        _cooldownStartUtc[processName] = DateTimeOffset.UtcNow;
    }

    private void StopCooldown(string processName)
    {
        _cooldownStartUtc.Remove(processName);
    }

    private void LogTargetStatus(
        string processName,
        bool isProcessRunning,
        HeartbeatInfo? heartbeat,
        bool isHeartbeatStale,
        UpdateLockInfo? updateLock,
        bool isUpdateLocked)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Process={Process} Running={Running}" + Environment.NewLine +
            "Heartbeat={HeartbeatState} (last={HeartbeatUtc:O}, now={NowUtc:O}, timeout={TimeoutSec}s)" + Environment.NewLine + 
            "UpdateLock={LockActive} (from={LockStartUtc:O}, until={LockExpireUtc:O}, reason={Reason})",
            processName,
            isProcessRunning,
            heartbeat == null ? "NONE" : isHeartbeatStale ? "STALE" : "OK",
            heartbeat?.TimestampUtc,
            nowUtc,
            _options.HeartbeatTimeoutSeconds,
            isUpdateLocked,
            updateLock?.StartedAtUtc,
            updateLock?.ExpiresAtUtc,
            updateLock?.Reason ?? "none"
        );

    }
}
