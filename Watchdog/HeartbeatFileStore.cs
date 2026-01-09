using System;
using System.IO;
using System.Text.Json;

namespace Watchdog;

public static class HeartbeatFileStore
{
    public static HeartbeatInfo? Read(string path)
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed record HeartbeatInfo(DateTimeOffset TimestampUtc, int Pid);
