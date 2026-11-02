using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item F: the roster-import backup runs BEFORE the write transaction; the snapshot is re-verified inside it.</summary>
internal static class StorageFix_RosterBackup
{
    private sealed class Hooks : IStoreFaults
    {
        internal readonly List<string> Order = new();
        internal Action<string>? OnBackup;
        public void AfterBegin(string op) { if (op == "apply-roster") Order.Add("begin"); }
        public void BeforeCommit(string op) { }
        public void AfterCommit(string op) { }
        public void BeforeBackup(string purpose) { Order.Add("backup:" + purpose); OnBackup?.Invoke(purpose); }
    }

    private const string Csv = "student_number,name,ip\n1,One,10.0.0.1\n2,Two,10.0.0.2";
    private static RosterImportPreview Preview(ChatStore store, string csv) => RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes(csv)));
    private static int BackupCount(string dir) => Directory.Exists(Path.Combine(dir, "backup")) ? Directory.GetFiles(Path.Combine(dir, "backup"), "nekochat-roster-*.db").Length : 0;

    internal static int Run()
    {
        var k = new Counter();

        // 1. order: backup first, write lock afterwards; a competing writer is never blocked during the backup
        using (var area = new Area())
        {
            var hooks = new Hooks(); bool writerFree = false;
            using var store = ChatStore.Open(new(area.Data) { Faults = hooks });
            string db = store.DatabasePath;
            hooks.OnBackup = _ =>
            {
                using var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
                other.Open();
                try { using var tx = other.BeginTransaction(deferred: false); tx.Rollback(); writerFree = true; }
                catch (SqliteException) { writerFree = false; }
            };
            var result = store.ApplyRosterPreview(Preview(store, Csv));
            k.Check(result is { Applied: true, Added: 2 }, "import applies");
            k.Check(hooks.Order.SequenceEqual(new[] { "backup:roster", "begin" }), "backup happens before the write transaction begins (" + string.Join(",", hooks.Order) + ")");
            k.Check(writerFree, "while the backup runs another writer can take the write lock immediately");
            k.Check(BackupCount(area.Data) == 1, "exactly one roster backup");
            var backupPath = Directory.GetFiles(Path.Combine(area.Data, "backup"), "nekochat-roster-*.db").Single();
            using var b = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            b.Open(); using var cmd = b.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM RosterEntry";
            k.Check(Convert.ToInt64(cmd.ExecuteScalar()) == 0, "the backup holds the pre-import roster (0 rows)");
        }

        // 2. roster changed between pre-flight and the lock: nothing applied, backup kept, typed result
        using (var area = new Area())
        {
            var hooks = new Hooks();
            using var store = ChatStore.Open(new(area.Data) { Faults = hooks });
            string db = store.DatabasePath;
            var preview = Preview(store, Csv);
            hooks.OnBackup = _ => Execute(db, "INSERT INTO RosterEntry(id,displayName,source) VALUES('00000000-0000-4000-8000-000000000001','Sneaky','manual')");
            var result = store.ApplyRosterPreview(preview);
            k.Check(result is { Applied: false, Reason: "roster_changed" }, "a change made after the pre-flight is detected inside the transaction");
            k.Check(Number(db, "SELECT count(*) FROM RosterEntry") == 1 && Number(db, "SELECT count(*) FROM RosterEntry WHERE displayName='Sneaky'") == 1, "nothing from the stale preview was applied");
            k.Check(BackupCount(area.Data) == 1, "the completed backup is retained");
        }

        // 3. stale / empty previews never create a backup
        using (var area = new Area())
        {
            var hooks = new Hooks();
            using var store = ChatStore.Open(new(area.Data) { Faults = hooks });
            var preview = Preview(store, Csv);
            store.SaveManualRosterEntry(null, new("Other"), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
            k.Check(store.ApplyRosterPreview(preview) is { Applied: false, Reason: "roster_changed" } && BackupCount(area.Data) == 0 && hooks.Order.Count == 0, "stale preview: refused before any backup or write transaction");
            var current = Preview(store, "student_number,name,ip\n9,Nine,10.0.0.9");
            store.ApplyRosterPreview(current);
            int backupsAfterFirst = BackupCount(area.Data);
            var again = Preview(store, "student_number,name,ip\n9,Nine,10.0.0.9");
            k.Check(again.Changes.Count == 0 && store.ApplyRosterPreview(again) is { Applied: true, Reason: "no-changes" } && BackupCount(area.Data) == backupsAfterFirst, "an import with no changes creates no backup");
        }
        return k.Count;
    }
}
