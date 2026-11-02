using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private void BackupBeforeRosterImport()
    {
        string? reserved = null;
        try
        {
            string directory = Path.Combine(_options.DataDirectory, "backup"); Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new StoreException(StoreErrorKind.CannotOpen, "The backup directory is a link.");
            string stamp = _options.UtcNow().ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            for (int i = 0; i < 100; i++)
            {
                string candidate = Path.Combine(directory, "nekochat-roster-" + stamp + (i == 0 ? "" : "-" + (i + 1).ToString(CultureInfo.InvariantCulture)) + ".db");
                try { using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } reserved = candidate; break; }
                catch (IOException) when (File.Exists(candidate)) { }
            }
            if (reserved is null) throw new StoreException(StoreErrorKind.CannotOpen, "No free backup file name.");
            using var source = NewConnection(create: false, readOnly: true);
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = reserved, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = _options.CommandTimeoutSeconds
            }.ToString());
            destination.Open(); source.BackupDatabase(destination);
            // The source is WAL. Make the backup a standalone database that readers do not turn into a sidecar set.
            if (!string.Equals(ScalarString(destination, "PRAGMA journal_mode=DELETE"), "delete", StringComparison.OrdinalIgnoreCase))
                throw new StoreException(StoreErrorKind.CannotOpen, "The backup could not be finalized.");
        }
        catch (Exception e)
        {
            if (reserved is not null)
                try { File.Delete(reserved); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            if (e is IOException or UnauthorizedAccessException)
                throw new StoreException(StoreErrorKind.CannotOpen, StoreErrors.Describe(StoreErrorKind.CannotOpen), e);
            throw;
        }
    }
}
