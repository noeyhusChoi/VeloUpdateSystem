namespace VeloUpdateSystem.Shared;

public static class IpcMessageTypes
{
    public const string Status = "Status";
    public const string Heartbeat = "Heartbeat";
    public const string PrepareToExit = "PrepareToExit";
    public const string WatchdogStatus = "WatchdogStatus";
    public const string Restart = "Restart";
    public const string ProcessMissing = "ProcessMissing";
    public const string ForceExit = "ForceExit";
}
