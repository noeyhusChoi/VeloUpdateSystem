namespace Watchdog;

public sealed class WatchdogOptions
{
    public const string SectionName = "Watchdog";

    public string AppProcessName { get; set; } = "VeloUpdateSystem";
    public string AgentProcessName { get; set; } = "UpdateAgent";
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int RestartWindowMinutes { get; set; } = 10;
    public int MaxRestartsInWindow { get; set; } = 5;
    public int BackoffMinutes { get; set; } = 30;
}
