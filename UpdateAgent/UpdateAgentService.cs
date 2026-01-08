using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Locators;

namespace UpdateAgent;

public sealed class UpdateAgentService : BackgroundService
{
    private readonly UpdateAgentOptions _options;
    private readonly AgentState _state;
    private readonly ILogger<UpdateAgentService> _logger;

    public UpdateAgentService(
        IOptions<UpdateAgentOptions> options,
        AgentState state,
        ILogger<UpdateAgentService> logger)
    {
        _options = options.Value;
        _state = state;
        _logger = logger;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.SetChannel(_options.Channel);

        _ = Task.Run(() => RunHttpServerAsync(stoppingToken), stoppingToken);
        _ = Task.Run(() => WatchdogStatusLoopAsync(stoppingToken), stoppingToken);

        await RunUpdateCheckAsync(stoppingToken).ConfigureAwait(false);

        var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.PollIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunUpdateCheckAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunHttpServerAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{_options.HttpPort}");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapGet("/status", () =>
        {
            var payload = BuildStatusPayload();
            return Results.Json(payload);
        });

        app.MapPost("/heartbeat", async (HttpContext context) =>
        {
        var request = await JsonSerializer.DeserializeAsync<HeartbeatRequest>(
            context.Request.Body,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

            if (request is not null)
            {
                _state.UpdateHeartbeat(DateTimeOffset.UtcNow, request.IdleMinutes, request.Pid, request.Responsive);
            }

            return Results.Ok();
        });

        _logger.LogInformation("HTTP IPC listening on http://127.0.0.1:{Port}", _options.HttpPort);
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private UpdateManager CreateUpdateManager()
    {
        var options = new UpdateOptions
        {
            ExplicitChannel = _options.Channel
        };

        var locator = CreateLocator();
        _logger.LogInformation("Update source: {Url}, channel: {Channel}.", _options.UpdateUrl, _options.Channel);
        _logger.LogInformation("Locator type: {LocatorType}.", locator.GetType().Name);
        return new UpdateManager(_options.UpdateUrl, options, locator);
    }

    private Velopack.Locators.IVelopackLocator CreateLocator()
    {
        var windowsLocator = new WindowsVelopackLocator(_options.PackId, (uint)Environment.ProcessId, null);
        LogLocatorDetails(windowsLocator);
        if (windowsLocator.CurrentlyInstalledVersion != null)
        {
            _logger.LogInformation(
                "Installed version detected: {Version}.",
                windowsLocator.CurrentlyInstalledVersion.ToString());
            return windowsLocator;
        }

        _logger.LogInformation(
            "App not installed for PackId {PackId}. WindowsVelopackLocator has no current version.",
            _options.PackId);
        return windowsLocator;
    }

    private async Task RunUpdateCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            _state.SetState("Checking");
            var manager = CreateUpdateManager();
            var currentVersion = manager.CurrentVersion?.ToString();
            _logger.LogInformation("Current version reported by UpdateManager: {Version}.", currentVersion ?? "unknown");
            _state.SetState("Checking", currentVersion: currentVersion);

            var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                _logger.LogInformation("No updates available.");
                _state.SetState("Idle", availableVersion: null);
                return;
            }

            var target = updateInfo.TargetFullRelease?.Version?.ToString();
            _logger.LogInformation("Update available: {Version}.", target ?? "unknown");
            _state.SetState("Downloading", availableVersion: target);

            await manager.DownloadUpdatesAsync(updateInfo, progress =>
            {
                _state.UpdateProgress(progress);
                _logger.LogDebug("Download progress: {Progress}%.", progress);
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

    private object BuildStatusPayload()
    {
        var snapshot = _state.GetSnapshot();
        return new
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
    }

    private async Task NotifyPrepareToExitAsync(string reason)
    {
        _logger.LogInformation("PrepareToExit requested: {Reason}", reason);
    }

    private async Task NotifyWatchdogRestartAsync(string reason)
    {
        _logger.LogWarning("Watchdog restart requested but IPC is disabled. Reason={Reason}", reason);
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

            _logger.LogDebug("Watchdog status skipped. appPid={AppPid} agentPid={AgentPid}", payload.appPid, payload.agentPid);
        }
    }

    private sealed record HeartbeatRequest(int Pid, bool Responsive, int IdleMinutes);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void LogLocatorDetails(IVelopackLocator locator)
    {
        var type = locator.GetType();
        var propertyNames = new[]
        {
            "RootAppDir",
            "AppDir",
            "PackagesDir",
            "UpdateExe",
            "UpdateExePath",
            "ProcessPath",
            "RootDir"
        };

        foreach (var name in propertyNames)
        {
            var property = type.GetProperty(name);
            if (property is null)
            {
                continue;
            }

            var value = property.GetValue(locator);
            if (value is not null)
            {
                _logger.LogInformation("Locator {Type} {Name}={Value}", type.Name, name, value);
            }
        }
    }

}
