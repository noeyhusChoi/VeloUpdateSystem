using System;
using System.IO;
using System.Text.Json;

namespace VeloUpdateSystem;

public sealed class AppSettings
{
    public string UpdateUrlTemplate { get; init; } = "http://4.218.15.147/releases/app/{channel}/";
    public string Channel { get; init; } = "stable";
    public int PollIntervalMinutes { get; init; } = 60;
    public int IdleSecondsBeforeApply { get; init; } = 60;
    public int WatchdogPort { get; init; } = 51235;

    public Uri WatchdogBaseUri => new($"http://127.0.0.1:{WatchdogPort}/");

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("App", out var app))
            {
                return new AppSettings();
            }

            return new AppSettings
            {
                UpdateUrlTemplate = GetString(app, "UpdateUrlTemplate") ?? "http://4.218.15.147/releases/app/{channel}/",
                Channel = GetString(app, "Channel") ?? "stable",
                PollIntervalMinutes = GetInt(app, "PollIntervalMinutes", 60),
                IdleSecondsBeforeApply = GetInt(app, "IdleSecondsBeforeApply", 60),
                WatchdogPort = GetInt(app, "WatchdogPort", 51235)
            };
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
    }

    public string GetUpdateUrl()
    {
        var url = UpdateUrlTemplate.Replace("{channel}", Channel, StringComparison.OrdinalIgnoreCase);
        return url.EndsWith("/") ? url : url + "/";
    }
}
