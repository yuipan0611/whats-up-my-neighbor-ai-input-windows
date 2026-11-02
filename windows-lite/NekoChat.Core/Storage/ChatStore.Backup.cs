using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private void BackupBeforeRosterImport()
    {
        // A separate reader avoids backing up the connection holding our write transaction.
        using var source = NewConnection(create: false, readOnly: true);
        CreateStoreBackup("roster", source);
    }

    private void CreateStoreBackup(string purpose, SqliteConnection source)
    {
        string? reserved = null;
        bool complete = false;
        try
        {
            string directory = Path.Combine(_options.DataDirectory, "backup"); Directory.CreateDirectory(directory);
            RequireBackupDirectory(directory);
            using var lease = AcquireBackupLease();
            DateTime now = _options.UtcNow().ToUniversalTime();
            string stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            for (int i = 0; i < 100; i++)
            {
                string candidate = Path.Combine(directory, "nekochat-" + purpose + "-" + stamp + (i == 0 ? "" : "-" + (i + 1).ToString(CultureInfo.InvariantCulture)) + ".db");
                if (new[] { candidate + "-wal", candidate + "-shm", candidate + "-journal" }.Any(p => File.Exists(p) || Directory.Exists(p))) continue;
                try { using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } reserved = candidate; break; }
                catch (IOException) when (File.Exists(candidate)) { continue; }   // name taken: never overwrite, try the next suffix
            }
            if (reserved is null) throw new StoreException(StoreErrorKind.CannotOpen, "No free backup file name.");
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = reserved, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = _options.CommandTimeoutSeconds
            }.ToString()))
            {
                destination.Open(); source.BackupDatabase(destination);
                if (!string.Equals(ScalarString(destination, "PRAGMA journal_mode=DELETE"), "delete", StringComparison.OrdinalIgnoreCase))
                    throw new StoreException(StoreErrorKind.CannotOpen, "The backup could not be finalized.");
            }
            complete = true;
            // The complete snapshot is retained even if the following app transaction fails.
            try { _ = MaintainBackupsLocked(directory, now, reserved); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or StoreException)
            { _options.DiagnosticSink?.Invoke("backup-maintenance failed=1"); }
        }
        catch (Exception e)
        {
            if (reserved is not null && !complete)
                try { File.Delete(reserved); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { _options.DiagnosticSink?.Invoke("backup-cleanup failed=1"); }
            if (e is IOException or UnauthorizedAccessException)
                throw new StoreException(StoreErrorKind.CannotOpen, StoreErrors.Describe(StoreErrorKind.CannotOpen), e);
            throw;
        }
    }
}
