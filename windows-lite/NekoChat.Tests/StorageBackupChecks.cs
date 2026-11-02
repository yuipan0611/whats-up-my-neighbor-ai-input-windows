using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageBackupChecks
{
    private const string ChildMode = "--storage-backup-lease-child";
    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 2) throw new ArgumentException("Invalid backup lease fixture.");
        using var store = ChatStore.Open(new(args[1]) { CommandTimeoutSeconds = 1 });
        try { _ = store.ListBackups(); Console.WriteLine("NOT-BUSY"); }
        catch (StoreException e) when (e.Kind == StoreErrorKind.StorageBusy) { Console.WriteLine("BUSY"); }
        return true;
    }
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Storage backup #{checks}: {name}"); }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); store.EnsureLocalIdentity("Local"); store.RegisterPeer("A", PairingState.Paired); store.ReceiveVerifiedMessage(new("A", "private", "Local", "PRIVATE-BODY-b49e433c"));
            string dbHash = Hash(f.Db), walHash = Hash(f.Db + "-wal");
            string oldest = f.Copy("v1", f.Now.AddDays(-15)), older = f.Copy("v1", f.Now.AddDays(-3)), latest = f.Copy("v1", f.Now.AddDays(-2));
            string expired = f.Copy("roster", f.Now.AddDays(-14)), roster = f.Copy("roster", f.Now.AddDays(-1));
            var result = store.MaintainBackups();
            Check(result is { Deleted: 3, Retained: 2, Failed: 0 } && !File.Exists(oldest) && !File.Exists(older) && !File.Exists(expired) && File.Exists(latest) && File.Exists(roster), "one latest per purpose; exact fourteen-day expiry");
            Check(store.ListBackups().Count == 2 && store.ListBackups().Select(b => b.Purpose).Order().SequenceEqual(new[] { "roster", "upgrade" }) && store.ListBackups().All(b => b.CreatedUtc.Kind == DateTimeKind.Utc && Path.GetFileName(b.FileName) == b.FileName),
                "materialized metadata only, normalized UTC and basename");
            Check(dbHash == Hash(f.Db) && walHash == Hash(f.Db + "-wal") && File.ReadAllText(f.Settings) == "settings sentinel" && File.ReadAllText(f.Key) == "private-key sentinel",
                "maintenance never touches main DB/WAL/settings/identity");
            Check(store.MaintainBackups() is { Deleted: 0, Retained: 2, Failed: 0 }, "cleanup idempotent");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = f.Copy("v1", f.Now), b = f.Copy("v1", f.Now, 2), c = f.Copy("v1", f.Now, 10);
            Check(store.MaintainBackups() is { Deleted: 2, Retained: 1 } && !File.Exists(a) && !File.Exists(b) && File.Exists(c), "numeric same-second suffix chooses latest copy");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); string future = f.Copy("v1", f.Now.AddDays(30));
            Check(store.MaintainBackups() is { Deleted: 0, Retained: 1 } && File.Exists(future), "future timestamp never treated as expired");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var protectedFiles = new List<string>();
            Directory.CreateDirectory(f.Backups);
            string foreign = Path.Combine(f.Backups, "my-personal-copy.db"); File.Copy(f.Seed1, foreign); protectedFiles.Add(foreign);
            string corrupt = f.PathFor("v1", f.Now.AddDays(-20)); File.WriteAllText(corrupt, "protected not a database"); protectedFiles.Add(corrupt);
            string empty = f.PathFor("roster", f.Now.AddDays(-21)); File.WriteAllBytes(empty, []); protectedFiles.Add(empty);
            string newer = f.Copy("roster", f.Now.AddDays(-22)); Execute(newer, "PRAGMA user_version=99"); protectedFiles.Add(newer);
            string mismatch = f.PathFor("v2", f.Now.AddDays(-23)); File.Copy(f.Seed1, mismatch); protectedFiles.Add(mismatch);
            string badDate = Path.Combine(f.Backups, "nekochat-v1-20260230T120000Z.db"); File.Copy(f.Seed1, badDate); protectedFiles.Add(badDate);
            foreach (string suffix in new[] { "0", "01", "101" })
            {
                string bad = Path.Combine(f.Backups, "nekochat-v1-20260101T120000Z-" + suffix + ".db"); File.Copy(f.Seed1, bad); protectedFiles.Add(bad);
            }
            string link = f.PathFor("v1", f.Now.AddDays(-24)); File.CreateSymbolicLink(link, f.Seed1); protectedFiles.Add(link);
            string wal = f.Copy("v1", f.Now.AddDays(-25)); File.WriteAllText(wal + "-wal", "active-wal sentinel"); protectedFiles.Add(wal); protectedFiles.Add(wal + "-wal");
            string journal = f.Copy("v1", f.Now.AddDays(-26)); File.WriteAllText(journal + "-journal", "journal sentinel"); protectedFiles.Add(journal); protectedFiles.Add(journal + "-journal");
            string linkedSidecar = f.Copy("v1", f.Now.AddDays(-27)); File.CreateSymbolicLink(linkedSidecar + "-shm", f.Key); protectedFiles.Add(linkedSidecar); protectedFiles.Add(linkedSidecar + "-shm");
            string altered = f.Copy("roster", f.Now.AddDays(-28)); Execute(altered, "CREATE TABLE ForeignExtra(x INTEGER)"); protectedFiles.Add(altered);
            var result = store.MaintainBackups();
            Check(result.Deleted == 0 && result.Ignored >= 13 && store.ListBackups().Count == 0 && protectedFiles.All(File.Exists), "unknown, empty, wrong version, newer, altered, linked and active backups preserved");
            Check(File.ReadAllText(f.Key) == "private-key sentinel" && File.ReadAllText(corrupt) == "protected not a database", "linked target and invalid file bytes unchanged");
        }
        using (var f = new Fixture())
        {
            using (var v1 = ChatStore.Open(f.Options with { Migrations = new[] { Migration.V1 } })) v1.EnsureLocalIdentity("Local");
            using (var v2 = ChatStore.Open(f.Options with { Migrations = new[] { Migration.V1, Migration.V2 } }))
            {
                var backup = v2.ListBackups().Single(); string path = Path.Combine(f.Backups, backup.FileName);
                Check(backup.Purpose == "upgrade" && Number(path, "PRAGMA user_version") == 1 && Text(path, "PRAGMA journal_mode") == "delete" && !File.Exists(path + "-wal"), "upgrade backup now standalone and readable v1");
            }
            f.Now = f.Now.AddHours(1);
            using var current = ChatStore.Open(f.Options);
            Check(current.ListBackups().Count == 1 && Number(Path.Combine(f.Backups, current.ListBackups().Single().FileName), "PRAGMA user_version") == 2,
                "new upgrade snapshot replaces older upgrade versions");
            var csv = RosterCsv.Parse(System.Text.Encoding.UTF8.GetBytes("student_number,name\n1,One"));
            current.ApplyRosterPreview(RosterImportPreview.Create(current.GetRosterSnapshot(), csv));
            Check(current.ListBackups().Count == 2 && current.ListBackups().Count(b => b.Purpose == "upgrade") == 1, "CSV backup does not erase upgrade rollback image");
            f.Now = f.Now.AddSeconds(1);
            current.ApplyRosterPreview(RosterImportPreview.Create(current.GetRosterSnapshot(), RosterCsv.Parse(System.Text.Encoding.UTF8.GetBytes("student_number,name\n1,Two"))));
            Check(current.ListBackups().Count == 2 && current.ListBackups().Count(b => b.Purpose == "roster") == 1, "creation automatically prunes older import backup");
            f.Now = f.Now.AddDays(-1);
            current.ApplyRosterPreview(RosterImportPreview.Create(current.GetRosterSnapshot(), RosterCsv.Parse(System.Text.Encoding.UTF8.GetBytes("student_number,name\n1,Three"))));
            var newestImport = current.ListBackups().Single(b => b.Purpose == "roster");
            Check(newestImport.CreatedUtc == f.Now && Text(Path.Combine(f.Backups, newestImport.FileName), "SELECT displayName FROM RosterEntry") == "Two",
                "newly created actual preimage wins even after backward clock change");
        }
        using (var f = new Fixture())
        {
            using (var store = ChatStore.Open(f.Options)) store.EnsureLocalIdentity("Local");
            f.Copy("v1", f.Now.AddDays(-13)); f.Copy("roster", f.Now.AddDays(-13));
            f.Now = f.Now.AddDays(1).AddSeconds(-1);
            using (var before = ChatStore.Open(f.Options)) Check(before.ListBackups().Count == 2, "startup one second before expiry keeps backups");
            f.Now = f.Now.AddSeconds(1);
            using var expired = ChatStore.Open(f.Options);
            Check(expired.ListBackups().Count == 0 && Number(f.Db, "SELECT count(*) FROM LocalIdentity") == 1, "startup at exact expiry cleans backups and preserves live DB");
        }
        using (var f = new Fixture())
        {
            var diagnostic = new List<string>(); using var store = ChatStore.Open(f.Options with { CommandTimeoutSeconds = 1, DiagnosticSink = diagnostic.Add }); Directory.CreateDirectory(f.Backups);
            var preview = RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(System.Text.Encoding.UTF8.GetBytes("student_number,name\n1,One")));
            using (var lease = new FileStream(Path.Combine(f.Data, ".backup-maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var clock = Stopwatch.StartNew(); var result = store.MaintainBackups();
                Check(result.Failed == 1 && clock.Elapsed >= TimeSpan.FromMilliseconds(800) && clock.Elapsed < TimeSpan.FromSeconds(4), "lease wait bounded and cleanup failure reported");
                StoreException? error = null; try { store.ApplyRosterPreview(preview); } catch (StoreException e) { error = e; }
                Check(error?.Kind == StoreErrorKind.StorageBusy && store.GetRosterSnapshot().Count == 0 && Directory.GetFiles(f.Backups).Length == 0, "busy backup lease aborts import without partial data or files");
                using var child = StartLeaseChild(f.Data);
                try
                {
                    if (!child.WaitForExit(6000)) throw new TimeoutException("Backup lease child timed out.");
                    Check(child.ExitCode == 0 && child.StandardOutput.ReadToEnd().Trim() == "BUSY", "separate process cannot acquire the held filesystem lease");
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); if (!child.WaitForExit(5000)) throw new TimeoutException("Backup lease child did not exit."); } }
            }
            Check(store.MaintainBackups().Failed == 0 && store.ApplyRosterPreview(preview).Applied && diagnostic.All(d => d == "backup-maintenance failed=1"), "lease release allows work; diagnostics contain counts only");
        }
        using (var f = new Fixture())
        {
            using (var v1 = ChatStore.Open(f.Options with { Migrations = new[] { Migration.V1 } })) v1.EnsureLocalIdentity("Local");
            string external = Path.Combine(f.Root, "external"); Directory.CreateDirectory(external); Directory.CreateSymbolicLink(f.Backups, external);
            StoreException? error = null; try { using var blocked = ChatStore.Open(f.Options); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.CannotOpen && Number(f.Db, "PRAGMA user_version") == 1 && Directory.GetFiles(external).Length == 0,
                "upgrade refuses linked backup directory before migration or data export");
        }
        Console.WriteLine($"Storage backup: {checks} retention, ownership, expiry and lease checks passed; 1 normal lease child."); return checks;
    }
    private static Process StartLeaseChild(string data)
    {
        string exe = Environment.ProcessPath!; var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(StorageBackupChecks).Assembly.Location);
        psi.ArgumentList.Add(ChildMode); psi.ArgumentList.Add(data);
        return Process.Start(psi) ?? throw new InvalidOperationException("Backup lease child did not start.");
    }
    private static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static object? Query(string db, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    private static long Number(string db, string sql) => Convert.ToInt64(Query(db, sql));
    private static string Text(string db, string sql) => Convert.ToString(Query(db, sql)) ?? "";
    private static void Execute(string db, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + db + ";Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-backup-" + Guid.NewGuid().ToString("N"));
        internal DateTime Now = new(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        internal string Data => Path.Combine(Root, "data"); internal string Db => Path.Combine(Data, "nekochat.db"); internal string Backups => Path.Combine(Data, "backup");
        internal string Seed1 => Path.Combine(Root, "seed1", "nekochat.db"); internal string Seed4 => Path.Combine(Root, "seed4", "nekochat.db");
        internal string Key => Path.Combine(Root, "identity", "synthetic.key"); internal string Settings => Path.Combine(Root, "settings.json");
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => Now };
        internal Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Key)!); File.WriteAllText(Key, "private-key sentinel"); File.WriteAllText(Settings, "settings sentinel");
            using (var v1 = ChatStore.Open(new(Path.GetDirectoryName(Seed1)!) { Migrations = new[] { Migration.V1 } })) v1.EnsureLocalIdentity("Seed");
            using var v4 = ChatStore.Open(new(Path.GetDirectoryName(Seed4)!)); v4.EnsureLocalIdentity("Seed");
        }
        internal string PathFor(string kind, DateTime time, int suffix = 1) => Path.Combine(Backups, "nekochat-" + kind + "-" + time.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + (suffix == 1 ? "" : "-" + suffix) + ".db");
        internal string Copy(string kind, DateTime time, int suffix = 1)
        {
            Directory.CreateDirectory(Backups); string path = PathFor(kind, time, suffix); File.Copy(kind == "roster" ? Seed4 : Seed1, path); return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
