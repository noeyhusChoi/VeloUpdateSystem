using System;
using System.IO;
using System.Text.Json;

namespace VeloUpdateSystem;

public sealed class WatchdogFileStore
{
    private readonly string _processName;
    private readonly string _watchdogDir;
    private readonly string _heartbeatPath;
    private readonly string _updateLockPath;

    public WatchdogFileStore(string processName)
    {
        _processName = processName;
        _watchdogDir = Path.Combine(AppContext.BaseDirectory, "watchdog");
        _heartbeatPath = Path.Combine(_watchdogDir, "heartbeat.json");
        _updateLockPath = Path.Combine(_watchdogDir, "update.lock.json");
    }

    public void WriteHeartbeat(int pid)
    {
        Directory.CreateDirectory(_watchdogDir);
        var payload = new HeartbeatInfo(DateTimeOffset.UtcNow, pid);
        WriteJsonAtomic(_heartbeatPath, payload);
    }

    public void WriteUpdateLock(TimeSpan ttl, string reason)
    {
        Directory.CreateDirectory(_watchdogDir);
        var expiresAt = ttl > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(ttl) : DateTimeOffset.MinValue;
        var payload = new UpdateLockInfo(DateTimeOffset.UtcNow, expiresAt, reason);
        WriteJsonAtomic(_updateLockPath, payload);
    }

    public void ClearExpiredUpdateLock()
    {
        if (!File.Exists(_updateLockPath))
        {
            return;
        }

        try
        {
            var payload = File.ReadAllText(_updateLockPath);
            var info = JsonSerializer.Deserialize<UpdateLockInfo>(payload, JsonOptions);
            if (info is null)
            {
                return;
            }

            if (info.ExpiresAtUtc != DateTimeOffset.MinValue &&
                DateTimeOffset.UtcNow > info.ExpiresAtUtc)
            {
                File.Delete(_updateLockPath);
            }
        }
        catch
        {
            // Leave lock in place if unreadable.
        }
    }

    public void ClearUpdateLock()
    {
        try
        {
            if (File.Exists(_updateLockPath))
            {
                File.Delete(_updateLockPath);
            }
        }
        catch
        {
        }
    }

    private static void WriteJsonAtomic<T>(string path, T payload)
    {
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Copy(tempPath, path, overwrite: true);
        File.Delete(tempPath);
    }

    private sealed record HeartbeatInfo(DateTimeOffset TimestampUtc, int Pid);
    private sealed record UpdateLockInfo(DateTimeOffset StartedAtUtc, DateTimeOffset ExpiresAtUtc, string Reason);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
