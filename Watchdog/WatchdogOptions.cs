namespace Watchdog;

public sealed class WatchdogOptions
{
    public const string SectionName = "Watchdog";

    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int RestartWindowMinutes { get; set; } = 10;
    public int MaxRestartsInWindow { get; set; } = 5;
    public int BackoffMinutes { get; set; } = 30;
    public List<WatchdogTarget> Targets { get; set; } = new();
}
