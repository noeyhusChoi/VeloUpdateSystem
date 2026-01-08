using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Watchdog;

public sealed class WatchdogService : BackgroundService
{
    private readonly WatchdogOptions _options;
    private readonly RestartLimiter _restartLimiter;
    private readonly ILogger<WatchdogService> _logger;
    private DateTimeOffset _lastAgentStatus = DateTimeOffset.MinValue;

    public WatchdogService(
        IOptions<WatchdogOptions> options,
        RestartLimiter restartLimiter,
        ILogger<WatchdogService> logger)
    {
        _options = options.Value;
        _restartLimiter = restartLimiter;
        _logger = logger;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!IsProcessRunning(_options.AgentProcessName))
            {
                await RestartProcessAsync(_options.AgentProcessName, "crash", minDelaySec: 0).ConfigureAwait(false);
            }

            if (!IsProcessRunning(_options.AppProcessName))
            {
                await RestartProcessAsync(_options.AppProcessName, "crash", minDelaySec: 0).ConfigureAwait(false);
            }

            await CheckAgentHeartbeatAsync().ConfigureAwait(false);
        }
    }

    private async Task CheckAgentHeartbeatAsync()
    {
        if (_lastAgentStatus == DateTimeOffset.MinValue)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        if (DateTimeOffset.UtcNow - _lastAgentStatus > timeout)
        {
            await RestartProcessAsync(_options.AgentProcessName, "heartbeatTimeout", minDelaySec: 0).ConfigureAwait(false);
        }
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

    private async Task RestartProcessAsync(string processName, string reason, int minDelaySec)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(_options.RestartWindowMinutes);

        if (!_restartLimiter.TryRegisterRestart(now, window, _options.MaxRestartsInWindow))
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
        await NotifyAgentProcessMissingAsync(processName).ConfigureAwait(false);
        await NotifyAppForceExitAsync(processName, reason).ConfigureAwait(false);
    }

    private async Task NotifyAgentProcessMissingAsync(string processName)
    {
        _logger.LogWarning("Process missing: {ProcessName}", processName);
    }

    private async Task NotifyAppForceExitAsync(string processName, string reason)
    {
        if (processName != _options.AppProcessName)
        {
            return;
        }

        _logger.LogWarning("Force exit requested: {Reason}", reason);
    }
}
