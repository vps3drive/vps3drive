namespace VPS3Drive.FileSystem;

public enum MountStatus
{
    NotInstalled,
    Stopped,
    Starting,
    Mounted,
    Stopping,
    Failed
}

public sealed record MountResult(MountStatus Status, string Message)
{
    public static MountResult NotAvailable(string message) => new(MountStatus.Stopped, message);
}
