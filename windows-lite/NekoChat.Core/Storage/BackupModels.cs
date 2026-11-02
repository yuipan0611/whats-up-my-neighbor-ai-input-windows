namespace NekoChat.Core.Storage;

public sealed record StoreBackupInfo(string FileName, string Purpose, DateTime CreatedUtc);
public sealed record BackupMaintenanceResult(int Deleted, int Retained, int Ignored, int Failed);
