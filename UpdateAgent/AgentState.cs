using System;
using System.Threading;

namespace UpdateAgent;

public sealed class AgentState
{
    private readonly object _lock = new();
    private string? _currentVersion;
    private string? _availableVersion;
    private string _channel = "stable";
    private string _state = "Idle";
    private int _progressPercent;
    private bool _hangDetected;
    private string? _lastErrorCode;
    private string? _lastErrorMessage;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private int _lastIdleMinutes;
    private int? _appPid;
    private bool _appResponsive;

    public (DateTimeOffset Timestamp, int IdleMinutes, int? AppPid, bool AppResponsive) GetHeartbeat()
    {
        lock (_lock)
        {
            return (_lastHeartbeat, _lastIdleMinutes, _appPid, _appResponsive);
        }
    }

    public void UpdateHeartbeat(DateTimeOffset timestamp, int idleMinutes, int? appPid, bool responsive)
    {
        lock (_lock)
        {
            _lastHeartbeat = timestamp;
            _lastIdleMinutes = idleMinutes;
            _appPid = appPid;
            _appResponsive = responsive;
        }
    }

    public void UpdateProgress(int percent)
    {
        lock (_lock)
        {
            _progressPercent = percent;
        }
    }

    public void SetState(string state, string? currentVersion = null, string? availableVersion = null)
    {
        lock (_lock)
        {
            _state = state;
            if (currentVersion != null)
            {
                _currentVersion = currentVersion;
            }
            if (availableVersion != null)
            {
                _availableVersion = availableVersion;
            }
        }
    }

    public void SetChannel(string channel)
    {
        lock (_lock)
        {
            _channel = channel;
        }
    }

    public void SetHangDetected(bool hangDetected)
    {
        lock (_lock)
        {
            _hangDetected = hangDetected;
        }
    }

    public void SetError(string code, string message)
    {
        lock (_lock)
        {
            _lastErrorCode = code;
            _lastErrorMessage = message;
        }
    }

    public StateSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new StateSnapshot(
                _currentVersion,
                _availableVersion,
                _channel,
                _state,
                _progressPercent,
                _hangDetected,
                _lastErrorCode,
                _lastErrorMessage
            );
        }
    }
}

public sealed record StateSnapshot(
    string? CurrentVersion,
    string? AvailableVersion,
    string Channel,
    string State,
    int ProgressPercent,
    bool HangDetected,
    string? LastErrorCode,
    string? LastErrorMessage
);
