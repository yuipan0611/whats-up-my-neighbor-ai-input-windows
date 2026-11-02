using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed record BackupRestoreResult(string Directory, string SourceSha256, int SourceVersion, int RestoredVersion,
    int SuspendedPeers, bool ContainsLocalIdentity, bool OutboundReadReceiptsEnabled);

/// <summary>Validates and prepares a standalone backup in a NEW directory. Never replaces existing data or imports keys.</summary>
public static class OfflineBackupRestore
{
    public static BackupRestoreResult Prepare(string backupFile, string newDirectory, CancellationToken cancellation = default) =>
        Prepare(backupFile, newDirectory, cancellation, null);

    internal static BackupRestoreResult Prepare(string backupFile, string newDirectory, CancellationToken cancellation, Action<string>? checkpoint)
    {
        string source = LocalPath(backupFile), target = Path.TrimEndingDirectorySeparator(LocalPath(newDirectory));
        string parent = Path.GetDirectoryName(target) ?? throw new IOException("Restore destination requires an existing parent.");
        RequireOrdinaryPath(source); RequireOrdinaryPath(parent);
        if (!File.Exists(source) || !Directory.Exists(parent) || Exists(target)) throw new IOException("Backup or new restore destination is unavailable.");
        RequireStandalone(source);
        string stage = Path.Combine(parent, ".nekochat-restore-" + Guid.NewGuid().ToString("N"));
        bool ownsStage = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (Exists(stage)) throw new IOException("Restore staging name is unavailable.");
            Directory.CreateDirectory(stage); ownsStage = true;
            string copy = Path.Combine(stage, "nekochat.db");
            var before = new FileInfo(source); long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
            // FileShare.None also prevents a cooperating writer/retention cleanup from modifying the source while we prepare it.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536]; int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read);
                    if (output.Position == read) checkpoint?.Invoke("copying");
                }
                output.Flush(flushToDisk: true);
            }
            string sourceHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            checkpoint?.Invoke("copied"); cancellation.ThrowIfCancellationRequested();
            int version;
            using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString()))
            {
                database.Open();
                version = checked((int)SchemaGuard.Inspect(database, Migration.Default.Count));
                if (version < 1 || Text(database, "PRAGMA journal_mode") != "delete") throw new InvalidDataException("Only a standalone finalized NekoChat backup can be restored.");
                Validate(database);
                if (Text(database, "PRAGMA journal_mode=WAL") != "wal") throw new IOException("Restore WAL initialization failed.");
            }
            int suspended = 0, restoredVersion; bool identity;
            // All schema migration, interrupted-attempt recovery and privacy changes happen only on our isolated copy.
            using (var restored = ChatStore.Open(new(stage)))
            {
                restoredVersion = restored.LatestVersion;
                var peers = restored.GetInboxSnapshot().Entries.Where(e => e.Pairing == PairingState.Paired && e.PeerId is not null).Select(e => e.PeerId!).Distinct(StringComparer.Ordinal).ToArray();
                foreach (string peer in peers) { cancellation.ThrowIfCancellationRequested(); restored.SetPairingState(peer, PairingState.Suspended); suspended++; }
                restored.SetReadReceiptPolicy(false);
                identity = restored.GetLocalIdentitySnapshot().Id is not null;
            }
            using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            { database.Open(); Validate(database); }
            checkpoint?.Invoke("prepared"); cancellation.ThrowIfCancellationRequested();
            input.Position = 0;
            if (Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != sourceHash) throw new IOException("Backup changed during restore.");
            before.Refresh();
            RequireOrdinaryPath(source); RequireOrdinaryPath(parent); RequireStandalone(source);
            if (before.Length != length || before.LastWriteTimeUtc != modified || Exists(target)) throw new IOException("Backup or destination changed during restore.");
            var result = new BackupRestoreResult(target, sourceHash, version, restoredVersion, suspended, identity, false);
            using (var report = new FileStream(Path.Combine(stage, "restore-report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(report, result, new JsonSerializerOptions { WriteIndented = true }); report.Flush(flushToDisk: true); }
            checkpoint?.Invoke("before-publish"); cancellation.ThrowIfCancellationRequested();
            // Same-parent rename exposes either no destination or the whole validated tree; no overwrite is permitted.
            Directory.Move(stage, target); ownsStage = false;
            checkpoint?.Invoke("published");
            return result;
        }
        finally
        {
            if (ownsStage)
                try { Directory.Delete(stage, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Validate(SqliteConnection database)
    {
        using var command = database.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        using (var rows = command.ExecuteReader())
            if (!rows.Read() || rows.GetString(0) != "ok" || rows.Read()) throw new InvalidDataException("Backup integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check";
        using var foreign = command.ExecuteReader();
        if (foreign.Read()) throw new InvalidDataException("Backup foreign keys are inconsistent.");
    }
    private static string Text(SqliteConnection database, string sql)
    { using var command = database.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar()) ?? ""; }
    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) throw new IOException("Network restore paths are unsupported.");
        string full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal)) throw new IOException("Network restore paths are unsupported.");
        return full;
    }
    private static bool Exists(string path)
    { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void RequireStandalone(string path)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            if (Exists(path + suffix)) throw new IOException("Restore source has an active or ambiguous sidecar.");
    }
    private static void RequireOrdinaryPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked restore paths are unsupported.");
    }
}
