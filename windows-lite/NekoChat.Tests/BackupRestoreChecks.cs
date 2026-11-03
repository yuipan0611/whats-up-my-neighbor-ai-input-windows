using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class BackupRestoreChecks
{
    private const string ChildMode = "--backup-restore-child";
    internal static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 4) throw new ArgumentException("Invalid restore fixture.");
        OfflineBackupRestore.Prepare(args[1], args[2], default, checkpoint => {
            if (checkpoint != args[3]) return;
            Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite);
        });
        throw new InvalidOperationException("Crash checkpoint was not reached.");
    }
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception($"Restore #{checks}: {name}"); }
        void Reject(Action action, string name)
        { bool rejected = false; try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or StoreException or SqliteException or OperationCanceledException) { rejected = true; } Check(rejected, name); }
        using (var f = new Fixture())
        {
            string backup = f.Seed(7); string hash = Hash(backup);
            var result = OfflineBackupRestore.Prepare(backup, f.Target);
            Check(result is { SourceVersion: 7, RestoredVersion: 7, SuspendedPeers: 2, ContainsLocalIdentity: true, OutboundReadReceiptsEnabled: false } && result.SourceSha256 == hash, "current backup reports verified hash, identity presence and reset security");
            using var restored = ChatStore.Open(new(f.Target));
            var entries = restored.GetInboxSnapshot().Entries;
            Check(entries.Where(e => e.PeerId is not null).All(e => e.Pairing == PairingState.Suspended), "every previously paired peer suspended on the restored copy");
            string conv = entries.Single(e => e.PeerId == "A").ConversationId!;
            Check(restored.ListMessages(conv).Items.Single().RawText == Fixture.Body && restored.GetDraftState(conv)?.RawText == Fixture.Draft, "actual messages and edited draft restored");
            Check(entries.Single(e => e.PeerId == "A").Attempt is { State: "unconfirmed", TryCount: 1 }, "interrupted send recovered without retransmission or fake confirmation");
            Check(!restored.GetReadReceiptPolicy().Enabled && restored.ListPendingReadReceipts().Count == 0, "outbound read policy off, historical pending read receipts dropped");
            Check(restored.ListLegacyHistories().Single().LastText == "unverified legacy", "separate legacy history preserved");
            string report = File.ReadAllText(Path.Combine(f.Target, "restore-report.json"));
            Check(!report.Contains(Fixture.Body) && !report.Contains(Fixture.Draft) && !Directory.Exists(Path.Combine(f.Target, "identity")), "restore report excludes content and no keys/settings imported");
            Check(hash == Hash(backup) && Number(backup, "SELECT count(*) FROM Peer WHERE pairingState='paired'") == 2 && Number(backup, "SELECT enabled FROM ReadReceiptPolicy") == 1, "source bytes and its trust/privacy settings unchanged");
        }
        for (int version = 1; version <= 7; version++)
        {
            using var f = new Fixture(); string backup = f.Seed(version), before = Hash(backup);
            var result = OfflineBackupRestore.Prepare(backup, f.Target);
            using var restored = ChatStore.Open(new(f.Target));
            Check(result.SourceVersion == version && result.RestoredVersion == 7 && restored.GetInboxSnapshot().Entries.Single(e => e.PeerId == "A").Pairing == PairingState.Suspended && Hash(backup) == before, $"frozen schema v{version} validates, upgrades, reopens and preserves source");
        }
        foreach (string kind in new[] { "empty", "text", "foreign", "newer", "altered", "foreign-key", "schema-record", "truncated", "wal-header" })
        {
            using var f = new Fixture(); string backup = f.Seed(7);
            switch (kind)
            {
                case "empty": File.WriteAllBytes(backup, []); break;
                case "text": File.WriteAllText(backup, "not a database"); break;
                case "foreign": File.Delete(backup); Execute(backup, "CREATE TABLE foreign_data(x)"); break;
                case "newer": Execute(backup, "PRAGMA user_version=99"); break;
                case "altered": Execute(backup, "CREATE TABLE foreign_extra(x)"); break;
                case "foreign-key": Execute(backup, "PRAGMA foreign_keys=OFF; DELETE FROM Peer WHERE id='B'"); break;
                case "schema-record": Execute(backup, "DELETE FROM SchemaVersion WHERE version=2"); break;
                case "truncated": using (var file = File.OpenWrite(backup)) file.SetLength(107); break;
                case "wal-header": Execute(backup, "PRAGMA journal_mode=WAL"); break;
            }
            string before = Hash(backup);
            Reject(() => OfflineBackupRestore.Prepare(backup, f.Target), "reject " + kind);
            Check(Hash(backup) == before && !Directory.Exists(f.Target) && !f.Stages().Any(), "failure preserves source and exposes no partial restore: " + kind);
        }
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            using var f = new Fixture(); string backup = f.Seed(7); File.WriteAllBytes(backup + suffix, []); string before = Hash(backup);
            Reject(() => OfflineBackupRestore.Prepare(backup, f.Target), "even empty/ambiguous sidecar rejected: " + suffix);
            Check(Hash(backup) == before && File.Exists(backup + suffix) && !Directory.Exists(f.Target), "sidecars never deleted: " + suffix);
        }
        using (var f = new Fixture())
        {
            string backup = f.Seed(7), link = Path.Combine(f.Root, "source-link.db"); File.CreateSymbolicLink(link, backup);
            Reject(() => OfflineBackupRestore.Prepare(link, f.Target), "linked source rejected");
            string parentLink = Path.Combine(f.Root, "parent-link"); Directory.CreateSymbolicLink(parentLink, Path.GetDirectoryName(backup)!);
            Reject(() => OfflineBackupRestore.Prepare(Path.Combine(parentLink, Path.GetFileName(backup)), f.Target), "source ancestor link rejected");
            Reject(() => OfflineBackupRestore.Prepare(backup, Path.Combine(parentLink, "output")), "destination ancestor link rejected");
            File.CreateSymbolicLink(f.Target, Path.Combine(f.Root, "missing"));
            Reject(() => OfflineBackupRestore.Prepare(backup, f.Target), "dangling destination link never overwritten");
            File.Delete(f.Target); Directory.CreateDirectory(f.Target); File.WriteAllText(Path.Combine(f.Target, "sentinel"), "keep");
            Reject(() => OfflineBackupRestore.Prepare(backup, f.Target), "existing destination tree never overwritten");
            Check(File.ReadAllText(Path.Combine(f.Target, "sentinel")) == "keep", "existing destination contents unchanged");
            Reject(() => OfflineBackupRestore.Prepare("//server/share/backup.db", Path.Combine(f.Root, "new")), "network source rejected before access");
            Reject(() => OfflineBackupRestore.Prepare(backup, "//server/share/output"), "network destination rejected before access");
        }
        using (var f = new Fixture())
        {
            string backup = f.Seed(7), before = Hash(backup);
            var original = new IOException("Synthetic preparation failure."); Exception? observed = null;
            try
            {
                OfflineBackupRestore.Prepare(backup, f.Target, default, point => {
                    if (point != "copied") return;
                    string stage = f.Stages().Single();
                    Directory.Move(stage, Path.Combine(f.Root, "held-stage"));
                    File.WriteAllText(stage, "synthetic cleanup obstruction");
                    throw original;
                });
            }
            catch (IOException error) { observed = error; }
            Check(ReferenceEquals(original, observed), "cleanup failure preserves the original exception");
            Check(observed?.Data["NekoChat.RestoreCleanupFailed"] is true, "incomplete staging cleanup is observable without private paths");
            Check(!Directory.Exists(f.Target) && Hash(backup) == before, "cleanup obstruction never publishes or changes the original backup");
        }
        foreach (string checkpoint in new[] { "copied", "prepared", "before-publish" })
        {
            using var f = new Fixture(); string backup = f.Seed(7), before = Hash(backup);
            using var cancel = new CancellationTokenSource();
            Reject(() => OfflineBackupRestore.Prepare(backup, f.Target, cancel.Token, point => { if (point == checkpoint) cancel.Cancel(); }), "cancel at " + checkpoint);
            Check(!Directory.Exists(f.Target) && !f.Stages().Any() && Hash(backup) == before, "cancel cleans only own staging and leaves source unchanged: " + checkpoint);
        }
        using (var f = new Fixture())
        {
            string backup = f.Seed(7);
            Reject(() => OfflineBackupRestore.Prepare(backup, f.Target, default, point => {
                if (point == "before-publish") { Directory.CreateDirectory(f.Target); File.WriteAllText(Path.Combine(f.Target, "sentinel"), "competing destination"); }
            }), "concurrent destination creation blocks rename");
            Check(File.ReadAllText(Path.Combine(f.Target, "sentinel")) == "competing destination" && !f.Stages().Any(), "failed publish never deletes the competitor's tree");
        }
        foreach (string checkpoint in new[] { "copying", "copied", "prepared", "before-publish", "published" })
        {
            using var f = new Fixture(); string backup = f.Seed(7), before = Hash(backup);
            using var child = StartChild(backup, f.Target, checkpoint); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { string? ready = await child.StandardOutput.ReadLineAsync(deadline.Token); if (ready != "READY") throw new Exception("Restore child checkpoint: " + await child.StandardError.ReadToEndAsync(deadline.Token)); }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            Check(Hash(backup) == before && Directory.Exists(f.Target) == (checkpoint == "published"), "actual process kill exposes only completed directory at " + checkpoint);
            if (checkpoint == "published")
            {
                using var restored = ChatStore.Open(new(f.Target));
                Check(restored.GetInboxSnapshot().Entries.Single(e => e.PeerId == "A") is { Pairing: PairingState.Suspended, Attempt.State: "unconfirmed" } && File.Exists(Path.Combine(f.Target, "restore-report.json")), "published restore survives process kill, reopens and remains network-suspended");
            }
            else Check(f.Stages().Count() == 1, "crash staging retained for operator inspection, never mistaken for restored data");
        }
        Console.WriteLine($"Offline backup restore: {checks} checks passed; five real killed child processes."); return checks;
    }
    private static Process StartChild(string backup, string target, string checkpoint)
    {
        string executable = Environment.ProcessPath!; var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(BackupRestoreChecks).Assembly.Location);
        foreach (string arg in new[] { ChildMode, backup, target, checkpoint }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new Exception("Restore child did not start.");
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private static void Execute(string path, string sql)
    { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long Number(string path, string sql)
    { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static SqliteConnection Open(string path)
    { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    private sealed class Fixture : IDisposable
    {
        internal const string Body = "RESTORE-private-body-🐈-493e";
        internal const string Draft = "RESTORE-unsent-draft-493e";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-restore-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Root, "restored");
        internal IEnumerable<string> Stages() => Directory.EnumerateDirectories(Root, ".nekochat-restore-*");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal string Seed(int version)
        {
            string original = Path.Combine(Root, "original"), backup = Path.Combine(Root, "backup.db");
            using (var store = ChatStore.Open(new(original) { Migrations = Migration.Default.Take(version).ToArray() }))
            {
                store.EnsureLocalIdentity("Local");
                var peer = (PeerRegistered)store.RegisterPeer("A", PairingState.Paired); store.RegisterPeer("B", PairingState.Paired);
                string conversation = peer.Conversation.ConversationId;
                if (version >= 2) store.ReceiveVerifiedMessage(new("A", "incoming", "Local", Body));
                store.SaveDraft(conversation, Draft, 0, 1); if (version >= 2) store.BeginManualSend(conversation, Draft, 0, 1);
                if (version >= 3) { store.SetReadReceiptPolicy(true); store.MarkVisibleRead(conversation, new[] { 1L }, new(conversation, true, true, false, true)); }
                if (version >= 6) store.ReceiveLegacyMessage(new("unverified legacy", null), store.GetLegacyReceiveContext("10.0.0.8"), () => true);
            }
            using (var source = Open(Path.Combine(original, "nekochat.db")))
            using (var destination = Open(backup))
            { source.BackupDatabase(destination); using var command = destination.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE"; command.ExecuteScalar(); }
            return backup;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
