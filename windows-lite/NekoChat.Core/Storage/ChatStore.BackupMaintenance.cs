using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private static readonly Regex BackupName = new(@"\Anekochat-(?<kind>v[1-9][0-9]*|roster)-(?<stamp>[0-9]{8}T[0-9]{6}Z)(?:-(?<suffix>[0-9]+))?\.db\z", RegexOptions.CultureInvariant);
    private sealed record BackupFile(string Path, StoreBackupInfo Info, int Suffix, long Length, DateTime ModifiedUtc);

    public IReadOnlyList<StoreBackupInfo> ListBackups()
    {
        lock (_gate)
        {
            ThrowIfDisposed(); string directory = Path.Combine(_options.DataDirectory, "backup");
            if (!Directory.Exists(directory)) return Array.Empty<StoreBackupInfo>();
            RequireBackupDirectory(directory); using var lease = AcquireBackupLease();
            return Array.AsReadOnly(ScanBackups(directory, out _, out _).OrderByDescending(b => b.Info.CreatedUtc).ThenByDescending(b => b.Suffix).Select(b => b.Info).ToArray());
        }
    }

    /// <summary>Run on startup, after creating a backup and from the app's daily maintenance task. Never blocks normal DB use on cleanup errors.</summary>
    public BackupMaintenanceResult MaintainBackups()
    {
        lock (_gate)
        {
            ThrowIfDisposed(); string directory = Path.Combine(_options.DataDirectory, "backup");
            if (!Directory.Exists(directory)) return new(0, 0, 0, 0);
            try
            {
                RequireBackupDirectory(directory); using var lease = AcquireBackupLease();
                return MaintainBackupsLocked(directory, _options.UtcNow().ToUniversalTime());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreException)
            {
                _options.DiagnosticSink?.Invoke("backup-maintenance failed=1"); return new(0, 0, 0, 1);
            }
        }
    }

    private BackupMaintenanceResult MaintainBackupsLocked(string directory, DateTime now, string? preferred = null)
    {
        var files = ScanBackups(directory, out int ignored, out int failed);
        var keep = files.Where(f => now < f.Info.CreatedUtc || now - f.Info.CreatedUtc < TimeSpan.FromDays(14))
            .GroupBy(f => f.Info.Purpose, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => f.Path == preferred).ThenByDescending(f => f.Info.CreatedUtc).ThenByDescending(f => f.Suffix).First().Path)
            .ToHashSet(StringComparer.Ordinal);
        int deleted = 0, retained = 0;
        foreach (var file in files)
        {
            if (keep.Contains(file.Path)) { retained++; continue; }
            bool removed = false;
            try
            {
                var current = new FileInfo(file.Path);
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.Length != file.Length || current.LastWriteTimeUtc != file.ModifiedUtc || HasActiveSidecars(file.Path))
                { ignored++; retained++; continue; }
                File.Delete(file.Path); deleted++; removed = true;
                foreach (string sidecar in new[] { file.Path + "-wal", file.Path + "-shm" })
                    if (File.Exists(sidecar) && (File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) == 0) File.Delete(sidecar);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed++; if (!removed) retained++; }
        }
        if (failed != 0) _options.DiagnosticSink?.Invoke("backup-maintenance failed=" + failed.ToString(CultureInfo.InvariantCulture));
        return new(deleted, retained, ignored, failed);
    }

    private List<BackupFile> ScanBackups(string directory, out int ignored, out int failed)
    {
        ignored = 0; failed = 0; var files = new List<BackupFile>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.db", SearchOption.TopDirectoryOnly))
        {
            var match = BackupName.Match(Path.GetFileName(path));
            if (!match.Success || !DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime created)) { ignored++; continue; }
            int suffix = 1;
            if (match.Groups["suffix"].Success && (!int.TryParse(match.Groups["suffix"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out suffix) || suffix is < 2 or > 100 ||
                suffix.ToString(CultureInfo.InvariantCulture) != match.Groups["suffix"].Value)) { ignored++; continue; }
            string kind = match.Groups["kind"].Value;
            if (kind != "roster" && !int.TryParse(kind.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _)) { ignored++; continue; }
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || HasActiveSidecars(path)) { ignored++; continue; }
                long length = file.Length; DateTime modified = file.LastWriteTimeUtc;
                using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString()))
                {
                    c.Open(); long version = SchemaGuard.Inspect(c, LatestVersion);
                    if (version < 1) { ignored++; continue; }
                    if (kind != "roster" && version.ToString(CultureInfo.InvariantCulture) != kind[1..]) { ignored++; continue; }
                }
                file.Refresh();
                if (file.Length != length || file.LastWriteTimeUtc != modified || HasActiveSidecars(path)) { ignored++; continue; }
                files.Add(new(path, new(file.Name, kind == "roster" ? "roster" : "upgrade", created), suffix, length, modified));
            }
            catch (StoreException) { ignored++; }
            catch (SqliteException e) when (e.SqliteErrorCode is 11 or 26 or 1) { ignored++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { failed++; }
        }
        return files;
    }

    private static bool HasActiveSidecars(string path) => File.Exists(path + "-journal") ||
        new[] { path + "-wal", path + "-shm" }.Any(p => File.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) ||
        File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length != 0;

    private static void RequireBackupDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new StoreException(StoreErrorKind.CannotOpen, "The backup directory is a link.");
    }

    private FileStream AcquireBackupLease()
    {
        string path = Path.Combine(_options.DataDirectory, ".backup-maintenance.lock"); var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new StoreException(StoreErrorKind.CannotOpen, "The backup lease is a link.");
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) when (File.Exists(path))
            {
                if (watch.Elapsed >= TimeSpan.FromSeconds(_options.CommandTimeoutSeconds))
                    throw new StoreException(StoreErrorKind.StorageBusy, "Backup maintenance is busy.", e);
                Thread.Sleep(20);
            }
        }
    }
}
