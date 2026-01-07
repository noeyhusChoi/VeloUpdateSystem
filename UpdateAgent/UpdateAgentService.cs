using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using VeloUpdateSystem.Shared;

namespace UpdateAgent;

public sealed class UpdateAgentService : BackgroundService
{
    private const string AppPipeName = "Moneybox.Agent";
    private const string WatchdogPipeName = "Moneybox.Watchdog";

    private readonly UpdateAgentOptions _options;
    private readonly AgentState _state;
    private readonly ILogger<UpdateAgentService> _logger;
    private readonly IpcServer _appServer;
    private readonly IpcServer _watchdogServer;

    public UpdateAgentService(
        IOptions<UpdateAgentOptions> options,
        AgentState state,
        ILogger<UpdateAgentService> logger)
    {
        _options = options.Value;
        _state = state;
        _logger = logger;
        _appServer = new IpcServer(AppPipeName, HandleAppMessageAsync);
        _watchdogServer = new IpcServer(WatchdogPipeName, HandleWatchdogMessageAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.SetChannel(_options.Channel);

        _ = _appServer.RunAsync(stoppingToken);
        _ = _watchdogServer.RunAsync(stoppingToken);
        _ = Task.Run(() => WatchdogStatusLoopAsync(stoppingToken), stoppingToken);

        await RunUpdateCheckAsync(stoppingToken).ConfigureAwait(false);

        var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.PollIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunUpdateCheckAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private UpdateManager CreateUpdateManager()
    {
        var options = new UpdateOptions
        {
            ExplicitChannel = _options.Channel
        };

        return new UpdateManager(_options.UpdateUrl, options, locator: null);
    }

    private async Task RunUpdateCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            _state.SetState("Checking");
            var manager = CreateUpdateManager();
            _state.SetState("Checking", currentVersion: manager.CurrentVersion?.ToString());

            var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                _state.SetState("Idle", availableVersion: null);
                return;
            }

            var target = updateInfo.TargetFullRelease?.Version?.ToString();
            _state.SetState("Downloading", availableVersion: target);

            await manager.DownloadUpdatesAsync(updateInfo, progress =>
            {
                _state.UpdateProgress(progress);
            }, cancellationToken).ConfigureAwait(false);

            _state.SetState("ReadyToInstall", availableVersion: target);
            await TryInstallAsync(manager, updateInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update check failed.");
            _state.SetError("E_UPDATE", ex.Message);
            _state.SetState("Error");
        }
    }

    private async Task TryInstallAsync(UpdateManager manager, UpdateInfo updateInfo, CancellationToken cancellationToken)
    {
        if (!IsAppIdle())
        {
            _logger.LogInformation("App is active, deferring install.");
            return;
        }

        await NotifyPrepareToExitAsync("updateInstall").ConfigureAwait(false);
        await WaitForAppExitAsync(cancellationToken).ConfigureAwait(false);

        _state.SetState("Installing");
        manager.ApplyUpdatesAndRestart(updateInfo, Array.Empty<string>());
        _state.SetState("RestartPending");

        await NotifyWatchdogRestartAsync("updateInstalled").ConfigureAwait(false);
    }

    private bool IsAppIdle()
    {
        var (timestamp, idleMinutes, _, responsive) = _state.GetHeartbeat();
        if (timestamp == DateTimeOffset.MinValue)
        {
            return false;
        }

        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        var stale = DateTimeOffset.UtcNow - timestamp > timeout;
        if (stale)
        {
            _state.SetHangDetected(true);
            return false;
        }

        _state.SetHangDetected(false);
        return responsive && idleMinutes >= _options.IdleMinutesBeforeInstall;
    }

    private async Task WaitForAppExitAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.GracefulExitTimeoutSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (!IsAppRunning())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        await NotifyWatchdogRestartAsync("forceExit").ConfigureAwait(false);
    }

    private bool IsAppRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(_options.AppProcessName);
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private Task<IpcEnvelope?> HandleAppMessageAsync(IpcEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case IpcMessageTypes.Status:
                return Task.FromResult<IpcEnvelope?>(CreateStatusEnvelope(envelope));
            case IpcMessageTypes.Heartbeat:
                HandleHeartbeat(envelope.Payload);
                return Task.FromResult<IpcEnvelope?>(null);
            default:
                return Task.FromResult<IpcEnvelope?>(null);
        }
    }

    private Task<IpcEnvelope?> HandleWatchdogMessageAsync(IpcEnvelope envelope)
    {
        if (envelope.Type == IpcMessageTypes.ProcessMissing)
        {
            _logger.LogWarning("Watchdog reported missing process: {Payload}", envelope.Payload.ToString());
        }

        return Task.FromResult<IpcEnvelope?>(null);
    }

    private void HandleHeartbeat(JsonElement payload)
    {
        var pid = payload.TryGetProperty("pid", out var pidElement) ? pidElement.GetInt32() : (int?)null;
        var responsive = payload.TryGetProperty("responsive", out var respElement) && respElement.GetBoolean();
        var idleMinutes = payload.TryGetProperty("idleMinutes", out var idleElement) ? idleElement.GetInt32() : 0;

        _state.UpdateHeartbeat(DateTimeOffset.UtcNow, idleMinutes, pid, responsive);
    }

    private IpcEnvelope CreateStatusEnvelope(IpcEnvelope request)
    {
        var snapshot = _state.GetSnapshot();
        var payload = new
        {
            appId = _options.PackId,
            packId = _options.PackId,
            currentVersion = snapshot.CurrentVersion,
            availableVersion = snapshot.AvailableVersion,
            channel = snapshot.Channel,
            state = snapshot.State,
            progress = new { percent = snapshot.ProgressPercent, bytes = 0, totalBytes = 0 },
            hangDetected = snapshot.HangDetected,
            lastError = new { code = snapshot.LastErrorCode, message = snapshot.LastErrorMessage }
        };

        return IpcEnvelope.Create(IpcMessageTypes.Status, "Agent", request.Source, payload, request.Id);
    }

    private async Task NotifyPrepareToExitAsync(string reason)
    {
        var payload = new { reason, timeoutSec = _options.GracefulExitTimeoutSeconds };
        var message = IpcEnvelope.Create(IpcMessageTypes.PrepareToExit, "Agent", "App", payload);

        foreach (var client in _appServer.GetClients())
        {
            await client.SendAsync(message).ConfigureAwait(false);
        }
    }

    private async Task NotifyWatchdogRestartAsync(string reason)
    {
        var payload = new { target = "App", reason, minDelaySec = 5 };
        var message = IpcEnvelope.Create(IpcMessageTypes.Restart, "Agent", "Watchdog", payload);
        try
        {
            await IpcClient.SendAsync(WatchdogPipeName, message, timeoutMs: 500, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send watchdog restart message.");
        }
    }

    private async Task WatchdogStatusLoopAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var (timestamp, _, appPid, _) = _state.GetHeartbeat();
            var payload = new
            {
                appPid,
                agentPid = Environment.ProcessId,
                appExpected = timestamp != DateTimeOffset.MinValue,
                appRunning = appPid.HasValue,
                hangDetected = _state.GetSnapshot().HangDetected
            };

            var message = IpcEnvelope.Create(IpcMessageTypes.WatchdogStatus, "Agent", "Watchdog", payload);
            try
            {
                await IpcClient.SendAsync(WatchdogPipeName, message, timeoutMs: 500, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Watchdog might not be running yet.
            }
        }
    }
}
