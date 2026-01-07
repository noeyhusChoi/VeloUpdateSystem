using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeloUpdateSystem.Shared;

namespace Watchdog;

public sealed class WatchdogService : BackgroundService
{
    private const string WatchdogPipeName = "Moneybox.Watchdog";
    private const string AppPipeName = "Moneybox.Watchdog.App";

    private readonly WatchdogOptions _options;
    private readonly RestartLimiter _restartLimiter;
    private readonly ILogger<WatchdogService> _logger;
    private readonly IpcServer _server;
    private DateTimeOffset _lastAgentStatus = DateTimeOffset.MinValue;

    public WatchdogService(
        IOptions<WatchdogOptions> options,
        RestartLimiter restartLimiter,
        ILogger<WatchdogService> logger)
    {
        _options = options.Value;
        _restartLimiter = restartLimiter;
        _logger = logger;
        _server = new IpcServer(WatchdogPipeName, HandleAgentMessageAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _server.RunAsync(stoppingToken);

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

    private Task<IpcEnvelope?> HandleAgentMessageAsync(IpcEnvelope envelope)
    {
        if (envelope.Type == IpcMessageTypes.WatchdogStatus)
        {
            _lastAgentStatus = DateTimeOffset.UtcNow;
        }
        else if (envelope.Type == IpcMessageTypes.Restart)
        {
            var target = envelope.Payload.TryGetProperty("target", out var targetElement)
                ? targetElement.GetString()
                : "App";
            var minDelaySec = envelope.Payload.TryGetProperty("minDelaySec", out var delayElement)
                ? delayElement.GetInt32()
                : 0;
            _ = RestartProcessAsync(
                target == "Agent" ? _options.AgentProcessName : _options.AppProcessName,
                "agentRequest",
                minDelaySec);
        }
        else if (envelope.Type == IpcMessageTypes.ProcessMissing)
        {
            _logger.LogWarning("Agent reported missing process: {Payload}", envelope.Payload.ToString());
        }

        return Task.FromResult<IpcEnvelope?>(null);
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
        var payload = new { target = processName == _options.AgentProcessName ? "Agent" : "App", missingSinceSec = 0 };
        var message = IpcEnvelope.Create(IpcMessageTypes.ProcessMissing, "Watchdog", "Agent", payload);
        try
        {
            await IpcClient.SendAsync(WatchdogPipeName, message, timeoutMs: 500, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Agent might be down.
        }
    }

    private async Task NotifyAppForceExitAsync(string processName, string reason)
    {
        if (processName != _options.AppProcessName)
        {
            return;
        }

        var payload = new { reason };
        var message = IpcEnvelope.Create(IpcMessageTypes.ForceExit, "Watchdog", "App", payload);
        try
        {
            await IpcClient.SendAsync(AppPipeName, message, timeoutMs: 500, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // App might be down already.
        }
    }
}
