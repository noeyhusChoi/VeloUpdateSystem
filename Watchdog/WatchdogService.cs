using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Watchdog;

public sealed class WatchdogService : BackgroundService
{
    private readonly WatchdogOptions _options;
    private readonly RestartLimiter _restartLimiter;
    private readonly ILogger<WatchdogService> _logger;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
    private int? _lastHeartbeatPid;
    private bool _lastHeartbeatResponsive;
    private int _lastHeartbeatIdleSeconds;
    private DateTimeOffset _updateSuppressUntilUtc = DateTimeOffset.MinValue;

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
        _ = Task.Run(() => RunHttpServerAsync(stoppingToken), stoppingToken);

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!IsUpdateSuppressed() && !IsProcessRunning(_options.AppProcessName))
            {
                await RestartProcessAsync(_options.AppProcessName, "crash", minDelaySec: 0).ConfigureAwait(false);
            }

            await CheckAppHeartbeatAsync().ConfigureAwait(false);

            _logger.LogDebug(
                "Watchdog tick: appRunning={AppRunning} lastHeartbeatUtc={HeartbeatUtc} updateSuppressed={Suppressed}",
                IsProcessRunning(_options.AppProcessName),
                _lastHeartbeatUtc == DateTimeOffset.MinValue ? null : _lastHeartbeatUtc,
                IsUpdateSuppressed());
        }
    }

    private async Task CheckAppHeartbeatAsync()
    {
        if (_lastHeartbeatUtc == DateTimeOffset.MinValue)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        if (DateTimeOffset.UtcNow - _lastHeartbeatUtc > timeout)
        {
            if (!IsUpdateSuppressed())
            {
                await RestartProcessAsync(_options.AppProcessName, "heartbeatTimeout", minDelaySec: 0).ConfigureAwait(false);
            }
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
        if (IsUpdateSuppressed())
        {
            _logger.LogWarning("Restart suppressed during update. Process={ProcessName} Reason={Reason}", processName, reason);
            return;
        }

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
        await NotifyAppForceExitAsync(processName, reason).ConfigureAwait(false);
    }

    private async Task NotifyAppForceExitAsync(string processName, string reason)
    {
        if (processName != _options.AppProcessName)
        {
            return;
        }

        _logger.LogWarning("Force exit requested: {Reason}", reason);
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

        app.MapPost("/heartbeat", async (HttpContext context) =>
        {
            var request = await JsonSerializer.DeserializeAsync<HeartbeatRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (request is not null)
            {
                _lastHeartbeatUtc = DateTimeOffset.UtcNow;
                _lastHeartbeatPid = request.Pid;
                _lastHeartbeatResponsive = request.Responsive;
                _lastHeartbeatIdleSeconds = request.IdleSeconds;
                _logger.LogInformation(
                    "Heartbeat received: pid={Pid} responsive={Responsive} idleSeconds={IdleSeconds}",
                    _lastHeartbeatPid,
                    _lastHeartbeatResponsive,
                    _lastHeartbeatIdleSeconds);
            }

            return Results.Ok();
        });

        app.MapPost("/update/start", () =>
        {
            _updateSuppressUntilUtc = DateTimeOffset.UtcNow.AddMinutes(_options.UpdateSuppressionMinutes);
            _logger.LogWarning(
                "Update suppression enabled until {UntilUtc}.",
                _updateSuppressUntilUtc);
            return Results.Ok();
        });

        app.MapPost("/update/end", () =>
        {
            _updateSuppressUntilUtc = DateTimeOffset.MinValue;
            _logger.LogWarning("Update suppression cleared.");
            return Results.Ok();
        });

        app.MapGet("/status", () =>
        {
            var payload = new
            {
                appProcess = _options.AppProcessName,
                appRunning = IsProcessRunning(_options.AppProcessName),
                lastHeartbeatUtc = _lastHeartbeatUtc == DateTimeOffset.MinValue ? (DateTimeOffset?)null : _lastHeartbeatUtc,
                lastHeartbeatPid = _lastHeartbeatPid,
                lastHeartbeatResponsive = _lastHeartbeatResponsive,
                lastHeartbeatIdleSeconds = _lastHeartbeatIdleSeconds,
                updateSuppressed = IsUpdateSuppressed()
            };
            return Results.Json(payload);
        });

        _logger.LogInformation("HTTP IPC listening on http://127.0.0.1:{Port}", _options.HttpPort);
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsUpdateSuppressed()
    {
        if (_updateSuppressUntilUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow <= _updateSuppressUntilUtc)
        {
            return true;
        }

        _updateSuppressUntilUtc = DateTimeOffset.MinValue;
        return false;
    }

    private sealed record HeartbeatRequest(int Pid, bool Responsive, int IdleSeconds);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
