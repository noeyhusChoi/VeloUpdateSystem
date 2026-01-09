using System;
using System.IO;
using System.Text.Json;

namespace Watchdog;

public static class UpdateLockFileStore
{
    public static UpdateLockInfo? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var payload = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdateLockInfo>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed record UpdateLockInfo(DateTimeOffset StartedAtUtc, DateTimeOffset ExpiresAtUtc, string? Reason);
