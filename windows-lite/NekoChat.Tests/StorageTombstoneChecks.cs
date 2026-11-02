using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageTombstoneChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Storage tombstone #{checks}: {name}"); }
        string root = Path.Combine(Path.GetTempPath(), "neko-tombstone-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string db = Path.Combine(root, "nekochat.db");
        try
        {
            string a;
            using (var v1 = ChatStore.Open(new(root) { Migrations = new[] { Migration.V1 } }))
            {
                v1.EnsureLocalIdentity("Local"); a = ((PeerRegistered)v1.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId;
                v1.SaveDraft(a, "preserve on upgrade", 0, 4);
            }
            string? forcedId = null; int allocations = 0;
            using (var upgraded = ChatStore.Open(new(root) { NewId = () => { allocations++; return forcedId ?? Guid.NewGuid().ToString("D"); } }))
            {
                Check(Number(db, "PRAGMA user_version") == 6 && upgraded.GetDraftState(a) is { RawText: "preserve on upgrade", Revision: 4 }, "real v1 upgrade preserves draft guards and body");
                string backup = Directory.GetFiles(Path.Combine(root, "backup"), "*.db").Single();
                Check(Number(backup, "PRAGMA user_version") == 1 && Number(backup, "SELECT count(*) FROM Draft") == 1, "upgrade creates readable original-v1 backup");
                Check(upgraded.ReceiveVerifiedMessage(new("A", "old", "Local", "original body", SourceEndpoint: "10.0.0.1")) is Stored, "incoming exists before clear");
                upgraded.ClearConversation(a);
                Check(upgraded.ReceiveVerifiedMessage(new("A", "old", "Local", "different body", SourceEndpoint: "10.0.0.2")) is ClearedDuplicate &&
                    Number(db, "SELECT count(*) FROM Message") == 0 && upgraded.GetUnreadCount(a) == 0 &&
                    Text(db, "SELECT lastSeenEndpoint FROM Peer WHERE id='A'") == "10.0.0.1", "cleared id cannot recreate body, unread, notification candidate or presence evidence");
                Check(upgraded.ReceiveVerifiedMessage(new("A", "old", "WrongLocal", "body")) is WrongRecipient, "current recipient checked before tombstones");
                upgraded.SetPairingState("A", PairingState.Revoked);
                Check(upgraded.ReceiveVerifiedMessage(new("A", "old", "Local", "body")) is UntrustedPeer, "revoked peer cannot probe tombstone result");
                upgraded.RegisterPeer("B", PairingState.Paired);
                Check(upgraded.ReceiveVerifiedMessage(new("B", "old", "Local", "other sender")) is Stored, "same id from another paired sender stays independent");
                Check(Number(db, "SELECT count(*) FROM pragma_table_info('ClearedMessage')") == 3 &&
                    Number(db, "SELECT count(*) FROM pragma_table_info('ClearedMessage') WHERE name IN ('senderId','messageId','clearedUtc')") == 3,
                    "tombstones contain exactly ids and clear timestamp");
                upgraded.SetPairingState("A", PairingState.Paired);
                var state = upgraded.GetDraftState(a)!;
                var attempt = (AttemptStarted)upgraded.BeginManualSend(a, "pending to clear", state.Epoch, state.Revision + 1);
                upgraded.ClearConversation(a); state = upgraded.GetDraftState(a)!;
                forcedId = attempt.MessageId; int beforeAllocations = allocations; bool exhausted = false;
                try { upgraded.BeginManualSend(a, "new draft", state.Epoch, state.Revision + 1); } catch (InvalidOperationException) { exhausted = true; }
                Check(exhausted && allocations - beforeAllocations == 3 && Number(db, "SELECT count(*) FROM ManualSendAttempt") == 0 &&
                    upgraded.GetDraftState(a) is { RawText: null } unchanged && unchanged.Revision == state.Revision,
                    "allocator cannot reuse a cleared pending-attempt id and rolls back proposed draft after bounded retries");
            }
            using (var reopened = ChatStore.Open(new(root))) Check(reopened.ReceiveVerifiedMessage(new("A", "old", "Local", "body")) is ClearedDuplicate, "tombstone survives reopen");
            using (var c = new SqliteConnection("Data Source=" + db + ";Pooling=False"))
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE ClearedMessage; PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery();
            }
            string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))); StoreException? failure = null;
            try { using var forged = ChatStore.Open(new(root)); } catch (StoreException e) { failure = e; }
            Check(failure?.Kind == StoreErrorKind.UnrecognizedDatabase && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))) == before,
                "forged current schema rejected read-only without recovery or pragma writes");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Storage tombstone: {checks} migration, privacy and resend checks passed."); return checks;
    }
    private static long Number(string db, string sql) => Convert.ToInt64(Query(db, sql));
    private static string Text(string db, string sql) => Convert.ToString(Query(db, sql)) ?? "";
    private static object? Query(string db, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
}
