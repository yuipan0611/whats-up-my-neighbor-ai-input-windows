using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item B: table-rebuild migrations (ForeignKeysOff path) and the NativeLibraryMissing mapping.</summary>
internal static class StorageFix_Migration
{
    private sealed class ThrowingFaults : IStoreFaults
    {
        internal Exception? OnBegin;
        public void AfterBegin(string op) { if (OnBegin is not null) throw OnBegin; }
        public void BeforeCommit(string op) { }
        public void AfterCommit(string op) { }
    }

    // Standard SQLite "12-step" rebuild of Conversation: keeps ids and the draft guard, re-creates its two triggers.
    private static Migration RebuildConversation(int version) => new(version, ctx => ctx.Exec(@"
CREATE TABLE Conversation_new(
  id TEXT PRIMARY KEY CHECK (length(id) BETWEEN 1 AND 64),
  peerId TEXT NOT NULL UNIQUE REFERENCES Peer(id) ON DELETE RESTRICT,
  draftEpoch INTEGER NOT NULL DEFAULT 0 CHECK (draftEpoch >= 0),
  draftRevision INTEGER NOT NULL DEFAULT 0 CHECK (draftRevision >= 0),
  createdUtc TEXT NOT NULL,
  note TEXT
) STRICT;
INSERT INTO Conversation_new(id,peerId,draftEpoch,draftRevision,createdUtc) SELECT id,peerId,draftEpoch,draftRevision,createdUtc FROM Conversation;
DROP TABLE Conversation;
ALTER TABLE Conversation_new RENAME TO Conversation;
CREATE TRIGGER Conversation_draftguard_monotonic
BEFORE UPDATE OF draftEpoch, draftRevision ON Conversation
WHEN NEW.draftEpoch < OLD.draftEpoch OR NEW.draftRevision < OLD.draftRevision
BEGIN SELECT RAISE(ABORT, 'Draft guard must not move backwards'); END;
CREATE TRIGGER Conversation_no_delete BEFORE DELETE ON Conversation
BEGIN SELECT RAISE(ABORT, 'Conversation rows are never deleted (draft guard lives here)'); END;"), ForeignKeysOff: true);

    private static IReadOnlyList<Migration> Plus(Migration extra) => Migration.Default.Append(extra).ToArray();

    internal static int Run()
    {
        var k = new Counter();
        using var area = new Area();
        string db;
        string conv;
        using (var store = ChatStore.Open(new(area.Data)))
        {
            db = store.DatabasePath;
            conv = ((PeerRegistered)store.RegisterPeer("P1", PairingState.Paired)).Conversation.ConversationId;
        }
        int latest = Migration.Default.Count;
        Execute(db, "UPDATE Conversation SET draftEpoch=3, draftRevision=7");
        Execute(db, "INSERT INTO Draft(conversationId,rawText,updatedUtc) VALUES(@c,'keep me','2026-11-03T00:00:00.000Z')", ("@c", conv));
        string guardBefore = Text(db, "SELECT id||'|'||peerId||'|'||draftEpoch||'|'||draftRevision||'|'||createdUtc FROM Conversation");

        // 1. a migration that fails halfway while foreign keys are OFF: everything rolls back, foreign keys come back ON
        var failing = new Migration(latest + 1, ctx => { ctx.Exec("CREATE TABLE Half(a)"); throw new InvalidOperationException("boom"); }, ForeignKeysOff: true);
        var error = Assert<StoreException>(() => ChatStore.Open(new(area.Data) { Migrations = Plus(failing) }).Dispose());
        k.Check(error is { Kind: StoreErrorKind.MigrationFailed } && error.FromVersion == latest && error.ToVersion == latest + 1, "failing ForeignKeysOff migration is reported with both versions");
        k.Check(Number(db, "PRAGMA user_version") == latest && Number(db, "SELECT count(*) FROM sqlite_master WHERE name='Half'") == 0, "failed migration rolled back (version unchanged, no Half table)");
        k.Check(Text(db, "SELECT id||'|'||peerId||'|'||draftEpoch||'|'||draftRevision||'|'||createdUtc FROM Conversation") == guardBefore, "failed migration leaves the draft guard untouched");

        // 2. a migration that leaves a foreign-key violation behind is rejected by the post-migration foreign_key_check
        var orphan = new Migration(latest + 1, ctx => ctx.Exec(
            "INSERT INTO Message(conversationId,direction,senderId,recipientId,messageId,rawText,fingerprintAlg,contentFingerprint,createdUtc) VALUES('no-such-conversation','incoming','P1','ME','m-orphan','x','a',zeroblob(32),'2026-11-03T00:00:00.000Z')"), ForeignKeysOff: true);
        var violation = Assert<StoreException>(() => ChatStore.Open(new(area.Data) { Migrations = Plus(orphan) }).Dispose());
        k.Check(violation is { Kind: StoreErrorKind.MigrationFailed } && Number(db, "SELECT count(*) FROM Message WHERE messageId='m-orphan'") == 0 && Number(db, "PRAGMA user_version") == latest,
            "foreign-key violation after a rebuild is rolled back");

        // 3. the real rebuild keeps ids, the draft guard, dependent rows and the guard triggers
        int backupsBefore = Directory.Exists(Path.Combine(area.Data, "backup")) ? Directory.GetFiles(Path.Combine(area.Data, "backup"), "nekochat-v*.db").Length : 0;
        using (var store = ChatStore.Open(new(area.Data) { Migrations = Plus(RebuildConversation(latest + 1)) }))
        {
            k.Check(Number(db, "PRAGMA user_version") == latest + 1 && Number(db, "SELECT count(*) FROM SchemaVersion") == latest + 1, "rebuild migration recorded");
            k.Check(Text(db, "SELECT id||'|'||peerId||'|'||draftEpoch||'|'||draftRevision||'|'||createdUtc FROM Conversation") == guardBefore, "rebuild preserves conversation id, peer, draft epoch/revision byte for byte");
            k.Check(Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", conv)) == "keep me", "dependent Draft row survives the rebuild");
            k.Check(Number(db, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name IN ('Conversation_draftguard_monotonic','Conversation_no_delete')") == 2, "guard triggers re-created");
            bool backward = false, deleted = false;
            try { Execute(db, "UPDATE Conversation SET draftEpoch=0"); } catch (SqliteException) { backward = true; }
            try { Execute(db, "DELETE FROM Conversation"); } catch (SqliteException) { deleted = true; }
            k.Check(backward && deleted, "guard still monotonic and undeletable after the rebuild");
            using (var c = Connect(db)) using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_key_check"; using var reader = cmd.ExecuteReader();
                k.Check(!reader.Read(), "foreign_key_check clean after the rebuild");
            }
            k.Check(Convert.ToInt64(store.DebugScalar("PRAGMA foreign_keys")) == 1, "foreign_keys is back ON on the store connection");
        }
        var backups = Directory.GetFiles(Path.Combine(area.Data, "backup"), "nekochat-v" + latest + "-*.db");
        k.Check(backups.Length == 1, "retention keeps exactly one upgrade backup (" + backups.Length + ", had " + backupsBefore + " before)");
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backups[0], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            old.Open(); using var cmd = old.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; var version = Convert.ToInt64(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT group_concat(draftEpoch||'.'||draftRevision) FROM Conversation";
            k.Check(version == latest && Convert.ToString(cmd.ExecuteScalar()) == "3.7", "the kept upgrade backup is the old schema and still holds the old data");
        }
        // Note: a reopen with only the default migration list is rejected by SchemaGuard (schema differs from v5 on purpose);
        // any real future migration must update SchemaGuard.CreateExpected in the same commit.
        k.Check(Assert<StoreException>(() => ChatStore.Open(new(area.Data)).Dispose()).Kind is StoreErrorKind.SchemaTooNew or StoreErrorKind.UnrecognizedDatabase, "a database newer than the known schema is refused");

        // 4. NativeLibraryMissing reaches callers as a typed StoreException on the real write path
        using var area2 = new Area();
        var faults = new ThrowingFaults();
        using (var store = ChatStore.Open(new(area2.Data) { Faults = faults }))
        {
            faults.OnBegin = new DllNotFoundException("e_sqlite3");
            k.Check(KindOf(() => store.RegisterPeer("P2", PairingState.Paired)) == StoreErrorKind.NativeLibraryMissing, "DllNotFoundException → NativeLibraryMissing");
            faults.OnBegin = new TypeInitializationException("SQLitePCL.raw", new DllNotFoundException());
            k.Check(KindOf(() => store.RegisterPeer("P2", PairingState.Paired)) == StoreErrorKind.NativeLibraryMissing, "TypeInitializationException → NativeLibraryMissing");
            faults.OnBegin = new InvalidOperationException("not a storage error");
            k.Check(Assert<InvalidOperationException>(() => store.RegisterPeer("P2", PairingState.Paired)) is not null, "unrelated exceptions are not disguised as storage errors");
            faults.OnBegin = null;
            k.Check(store.RegisterPeer("P2", PairingState.Paired) is PeerRegistered { Created: true }, "the store keeps working after those failures");
        }
        return k.Count;
    }

    private static T Assert<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        throw new Exception("expected " + typeof(T).Name);
    }
}
