using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class SecurityRevisionChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Security revision #{checks}: {name}"); }
        void Constraint(string path, string sql, string name)
        {
            bool rejected = false; try { Execute(path, sql); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { rejected = true; }
            Check(rejected, name);
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); string row = Seed(store, f.Key);
            var before = store.GetPeerSecuritySnapshot("A"); var target = store.GetPresenceTargets().Single();
            Check(before is { LocalRevision: 1, PeerRevision: 1, PeerExists: true, Pairing: PairingState.Paired } && target.RosterRevision == 1,
                "insert and meaningful trust/binding changes have hand-counted revisions");
            store.SetPairingState("A", PairingState.Paired); store.RegisterPeer("A", PairingState.Paired, f.Key);
            store.TouchPeerSeen("A", "10.0.0.1"); store.ApplyPresenceEvidence("A", "10.0.0.1", true);
            store.SaveManualRosterEntry(row, new("Renamed", "001", true, "10.0.0.1"), Token(store));
            var conversation = ((PeerRegistered)store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId; store.SaveDraft(conversation, "draft", 0, 1);
            Check(store.GetPeerSecuritySnapshot("A") == before && store.GetPresenceTargets().Single().RosterRevision == target.RosterRevision,
                "same trust state, labels, provenance, draft and seen/install timestamps do not churn security revisions");
            store.SetPairingState("A", PairingState.Revoked); store.SetPairingState("A", PairingState.Paired);
            Check(store.GetPeerSecuritySnapshot("A").PeerRevision == before.PeerRevision + 2, "trust ABA increments on both edges");
            store.DeleteRosterEntry(row, Token(store));
            Execute(f.Db, "INSERT INTO RosterEntry(id,displayName,studentNumber,peerId,endpoint,source) VALUES(@id,'Renamed','001','A','10.0.0.1','manual')", ("@id", row));
            Check(store.GetPresenceTargets().Single().RosterRevision == target.RosterRevision + 2, "delete/readd same roster id never reuses revision");
            long revision = store.GetPresenceTargets().Single().RosterRevision; string alternate = Guid.NewGuid().ToString("D");
            Execute(f.Db, "UPDATE RosterEntry SET id=@next WHERE id=@old; UPDATE RosterEntry SET id=@old WHERE id=@next", ("@next", alternate), ("@old", row));
            Check(store.GetPresenceTargets().Single().RosterRevision == revision + 2, "roster id rename and return preserve tombstoned revision history");
            Execute(f.Db, "INSERT INTO Peer(id,pairingState,createdUtc) VALUES('ephemeral','pending','t'); DELETE FROM Peer WHERE id='ephemeral'; INSERT INTO Peer(id,pairingState,createdUtc) VALUES('ephemeral','pending','t')");
            Check(store.GetPeerSecuritySnapshot("ephemeral").PeerRevision == 2, "peer delete/readd never reuses revision");
            Execute(f.Db, "UPDATE Peer SET id='temporary-peer' WHERE id='ephemeral'; UPDATE Peer SET id='ephemeral' WHERE id='temporary-peer'");
            Check(store.GetPeerSecuritySnapshot("ephemeral").PeerRevision == 4, "unreferenced peer rename and return fenced");
            Constraint(f.Db, "DELETE FROM RosterSecurityRevision", "revision tombstones cannot be deleted");
            Constraint(f.Db, "UPDATE PeerSecurityRevision SET revision=0 WHERE peerId='A'", "revisions cannot decrease");
            Constraint(f.Db, "UPDATE PeerSecurityRevision SET peerId='moved' WHERE peerId='A'", "revision keys cannot be renamed");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var first = ChatStore.Open(f.Options with { Faults = faults }); Seed(first, f.Key); using var second = ChatStore.Open(f.Options);
            var captured = first.GetPeerSecuritySnapshot("A"); second.SetPairingState("A", PairingState.Revoked); second.SetPairingState("A", PairingState.Paired);
            var stale = first.SetPairingStateIfCurrent(captured, PairingState.Suspended, () => true);
            Check(stale is { Changed: false, Reason: "stale-security", Current.Pairing: PairingState.Paired } && stale.Current.PeerRevision > captured.PeerRevision,
                "transactional security CAS rejects ABA from another handle");
            var changed = first.SetPairingStateIfCurrent(first.GetPeerSecuritySnapshot("A"), PairingState.Suspended, () => true);
            Check(changed is { Changed: true, Current.Pairing: PairingState.Suspended } && changed.Current.PeerRevision == stale.Current.PeerRevision + 1,
                "fresh guarded transition returns its committed revision");
            bool live = true; var before = first.GetPeerSecuritySnapshot("A");
            faults.Before = op => { if (op == "security-change") live = false; };
            var cancelled = first.SetPairingStateIfCurrent(before, PairingState.Paired, () => live); faults.Before = null;
            Check(cancelled is { Changed: false, Reason: "stale-security" } && first.GetPeerSecuritySnapshot("A") == before && cancelled.Current == before,
                "session invalidation at final commit rolls back state and revision and returns actual old snapshot");
            faults.Before = op => { if (op == "security-change") throw new IOException("injected security rollback"); };
            bool failed = false; try { first.SetPairingStateIfCurrent(before, PairingState.Paired, () => true); } catch (IOException) { failed = true; } faults.Before = null;
            Check(failed && first.GetPeerSecuritySnapshot("A") == before, "failed security persistence does not consume revision or state");
            var unknown = first.GetPeerSecuritySnapshot("Missing");
            Check(first.SetPairingStateIfCurrent(unknown, PairingState.Paired, () => true).Reason == "unknown-peer" && !first.GetPeerSecuritySnapshot("Missing").PeerExists,
                "CAS never creates unknown peers");
            first.RegisterPeer("NoPin", PairingState.Pending);
            Check(first.SetPairingStateIfCurrent(first.GetPeerSecuritySnapshot("NoPin"), PairingState.Paired, () => true).Reason == "identity-not-provisioned", "wire-facing CAS cannot pair without pinned key");
            before = first.GetPeerSecuritySnapshot("A"); first.ReplaceLocalIdentity("NewLocal", f.Key, "ref-new");
            Check(first.SetPairingStateIfCurrent(before, PairingState.Paired, () => true).Reason == "stale-security", "local identity revision also fences trust transitions");
        }
        using (var f = new Fixture())
        {
            using var first = ChatStore.Open(f.Options); Seed(first, f.Key); using var second = ChatStore.Open(f.Options); var captured = first.GetPeerSecuritySnapshot("A");
            using var start = new ManualResetEventSlim();
            var work = new[] { (Store: first, State: PairingState.Suspended), (Store: second, State: PairingState.Revoked) }.Select(x => Task.Factory.StartNew(() => {
                if (!start.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("security start gate");
                return x.Store.SetPairingStateIfCurrent(captured, x.State, () => true);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            start.Set(); var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(12));
            Check(results.Count(r => r.Changed) == 1 && results.Count(r => r.Reason == "stale-security") == 1 && first.GetPeerSecuritySnapshot("A").PeerRevision == captured.PeerRevision + 1,
                "two handles competing on one security snapshot commit exactly one transition");
        }
        foreach (string area in new[] { "local", "peer", "roster" })
        {
            using var f = new Fixture(); using var store = ChatStore.Open(f.Options); string row = Seed(store, f.Key);
            string table = area == "local" ? "LocalSecurityRevision" : area == "peer" ? "PeerSecurityRevision" : "RosterSecurityRevision";
            Execute(f.Db, "UPDATE " + table + " SET revision=" + long.MaxValue);
            StoreException? error = null;
            try
            {
                if (area == "local") store.ReplaceLocalIdentity("NewLocal", f.Key, "new-ref");
                else if (area == "peer") store.SetPairingState("A", PairingState.Revoked);
                else store.SaveManualRosterEntry(row, new("Changed", "2", true, "10.0.0.2"), Token(store));
            }
            catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.ConstraintViolation && Number(f.Db, "SELECT revision FROM " + table + " LIMIT 1") == long.MaxValue &&
                Text(f.Db, "SELECT id FROM LocalIdentity") == "Local" && Text(f.Db, "SELECT pairingState FROM Peer WHERE id='A'") == "paired" &&
                Text(f.Db, "SELECT endpoint FROM RosterEntry") == "10.0.0.1" && Number(f.Db, "SELECT count(*) FROM LocalIdentityHistory") == 0,
                "STRICT counter exhaustion fails and rolls back the whole actor mutation without wrapping");
        }
        using (var f = new Fixture())
        {
            using (var old = ChatStore.Open(f.Options with { Migrations = new[] { Migration.V1, Migration.V2, Migration.V3, Migration.V4 } }))
            {
                Seed(old, f.Key); old.SetReadReceiptPolicy(true); old.ImportLegacyRoster([]);
                var conversation = ((PeerRegistered)old.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId;
                old.SaveDraft(conversation, "preserve upgrade draft", 0, 3);
                var received = (Stored)old.ReceiveVerifiedMessage(new("A", "m", "Local", "preserve upgrade history"));
                old.MarkVisibleRead(conversation, [received.LocalSeq], new(conversation, true, true, false, true));
            }
            using var upgraded = ChatStore.Open(f.Options);
            Check(Number(f.Db, "PRAGMA user_version") == 10 && upgraded.GetPeerSecuritySnapshot("A") is { LocalRevision: 0, PeerRevision: 0 } && upgraded.GetPresenceTargets().Single().RosterRevision == 0,
                "real v4 upgrade seeds existing actors without rewriting identity or binding");
            Check(upgraded.GetReadReceiptPolicy().Enabled && upgraded.LegacyRosterImportCompleted() && upgraded.ListPendingReadReceipts().Count == 1 && Number(f.Db, "SELECT count(*) FROM Message") == 1 && Text(f.Db, "SELECT rawText FROM Draft") == "preserve upgrade draft",
                "v5 migration preserves privacy preference, bootstrap marker, pending receipt, history and draft");
            string backup = Path.Combine(f.Data, "backup", upgraded.ListBackups().Single().FileName);
            Check(Number(backup, "PRAGMA user_version") == 4 && Text(backup, "SELECT rawText FROM Draft") == "preserve upgrade draft", "upgrade backup retains real previous schema and data");
        }
        using (var f = new Fixture())
        {
            var v4 = new[] { Migration.V1, Migration.V2, Migration.V3, Migration.V4 };
            using (var old = ChatStore.Open(f.Options with { Migrations = v4 })) Seed(old, f.Key);
            var broken = v4.Concat(new[] { new Migration(5, ctx => { ctx.Exec(SecurityRevisionSchema.Sql); throw new IOException("injected v5 migration failure"); }) }).ToArray();
            StoreException? error = null; try { using var fail = ChatStore.Open(f.Options with { Migrations = broken }); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.MigrationFailed && Number(f.Db, "PRAGMA user_version") == 4 && Number(f.Db, "SELECT count(*) FROM sqlite_master WHERE name='LocalSecurityRevision'") == 0 && Text(f.Db, "SELECT id FROM LocalIdentity") == "Local",
                "failed actual revision DDL rolls back all v5 tables/triggers and retains v4 data");
        }
        foreach (string table in new[] { "LocalSecurityRevision", "PeerSecurityRevision", "RosterSecurityRevision" })
        {
            using var f = new Fixture();
            using (var store = ChatStore.Open(f.Options)) Seed(store, f.Key);
            // Keep the schema definition identical while injecting a missing value row.
            Execute(f.Db, $"DROP TRIGGER {table}_no_delete; DELETE FROM {table}; CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT,'Security revision cannot be deleted'); END; PRAGMA wal_checkpoint(TRUNCATE)");
            string before = Hash(f.Db); StoreException? error = null; try { using var invalid = ChatStore.Open(f.Options); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.Corrupt && Hash(f.Db) == before, "missing live security revision rejected read-only before recovery or PRAGMA writes");
        }
        Console.WriteLine($"Security revision: {checks} durable epochs, CAS, rollback and migration checks passed."); return checks;
    }

    private static string Seed(ChatStore store, byte[] key)
    {
        store.EnsureLocalIdentity("Local", key, "fixture-key-ref"); store.RegisterPeer("A", PairingState.Pending, key); store.SetPairingState("A", PairingState.Paired);
        string row = store.SaveManualRosterEntry(null, new("Name", "1", false, "10.0.0.1"), Token(store)).EntryId!; store.BindPairedRosterPeer(row, "A", Token(store)); return row;
    }
    private static string Token(ChatStore store) => RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
    private static object? Query(string path, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    private static long Number(string path, string sql) => Convert.ToInt64(Query(path, sql));
    private static string Text(string path, string sql) => Convert.ToString(Query(path, sql)) ?? "";
    private static string Hash(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s));
    }
    private static void Execute(string path, string sql, params (string Name, object Value)[] args)
    {
        using var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON;" + sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Name, arg.Value); cmd.ExecuteNonQuery();
    }
    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-security-" + Guid.NewGuid().ToString("N")); internal string Db => Path.Combine(Data, "nekochat.db");
        internal byte[] Key { get; }
        internal ChatStoreOptions Options => new(Data);
        internal Fixture() { Directory.CreateDirectory(Data); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); Key = key.ExportSubjectPublicKeyInfo(); }
        public void Dispose() => Directory.Delete(Data, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
