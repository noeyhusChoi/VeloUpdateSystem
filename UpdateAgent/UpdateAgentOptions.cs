namespace UpdateAgent;

public sealed class UpdateAgentOptions
{
    public const string SectionName = "UpdateAgent";

    public string UpdateUrl { get; set; } = "";
    public string PackId { get; set; } = "VeloUpdateSystem";
    public string Channel { get; set; } = "stable";
    public int PollIntervalMinutes { get; set; } = 60;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int IdleMinutesBeforeInstall { get; set; } = 10;
    public int GracefulExitTimeoutSeconds { get; set; } = 30;
    public string AppProcessName { get; set; } = "VeloUpdateSystem";
}
