using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageFoundationChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string text)
        {
            checks++;
            if (!condition) throw new Exception($"Storage foundation #{checks}: {text}");
        }
        void Constraint(string path, string sql, string text)
        {
            bool failed = false;
            try { Execute(path, sql); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { failed = true; }
            Check(failed, text);
        }
        void Error(Action action, StoreErrorKind kind, string text)
        {
            StoreException? failure = null;
            try { action(); } catch (StoreException e) { failure = e; }
            Check(failure?.Kind == kind, text);
        }

        using (var area = new Area())
        {
            string settings = Path.Combine(area.Root, "settings.json");
            File.WriteAllText(settings, "{\"Contacts\":[{\"Ip\":\"192.168.1.2\",\"Name\":\"Synthetic\"}]}");
            string settingsHash = Hash(settings);
            using (var store = ChatStore.Open(new(area.Data)))
            {
                string path = store.DatabasePath;
                Check(Number(path, "PRAGMA user_version") == 10, "new database uses schema v10");
                Check(Number(path, "SELECT count(*) FROM SchemaVersion") == 10, "ten migrations recorded");
                Check(Text(path, "PRAGMA journal_mode") == "wal", "WAL persisted");
                Check(Number(path, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT GLOB 'sqlite_*'") == 26, "all twenty-six STRICT tables exist");
                Check(Number(path, "SELECT sum(strict) FROM pragma_table_list WHERE name NOT GLOB 'sqlite_*'") == 26, "all application tables STRICT");
                Check(Number(path, "SELECT count(*) FROM Peer") == 0, "old settings never auto-pair");
                using (var reader = store.OpenReadConnectionForTest())
                {
                    Check(Number(reader, "PRAGMA foreign_keys") == 1, "read connection foreign keys");
                    Check(Number(reader, "PRAGMA synchronous") == 2, "read connection synchronous FULL");
                    Check(Number(reader, "PRAGMA trusted_schema") == 0, "read connection trusted_schema OFF");
                    Check(Number(reader, "PRAGMA secure_delete") == 1, "read connection secure_delete ON");
                    Check(Number(reader, "PRAGMA journal_size_limit") == 4_194_304, "journal_size_limit configured per connection");
                    Check(Number(reader, "PRAGMA query_only") == 1, "read helper matches read-only query path");
                }
                Check(store.EnsureLocalIdentity("Local_A", new byte[] { 1, 2 }, "ref-A") == "Local_A", "identity provisioned");
                Check(store.EnsureLocalIdentity("Ignored", new byte[] { 9 }, "ignored") == "Local_A" &&
                    Text(path, "SELECT credentialRef FROM LocalIdentity") == "ref-A", "ensure never overwrites existing identity");
                var probe = new Probe();
                Check(store.CheckLocalIdentity(probe) is IdentityConsistent, "matching key probe");
                Check(store.CheckLocalIdentity(probe, _ => "Wrong") is IdentityMismatch { Reason: "id-not-derived" }, "derived identity mismatch");
                Check(store.CheckLocalIdentity(new Probe { Key = new byte[] { 4 } }) is IdentityMismatch { Reason: "public-key-differs" }, "public key mismatch");
                Check(store.CheckLocalIdentity(new Probe { Exists = false }) is IdentityLost { Reason: "key-missing" }, "missing credential");
                Check(store.CheckLocalIdentity(new Probe { Readable = false }) is IdentityLost { Reason: "public-key-unreadable" }, "unreadable public key");
                Check(store.CheckLocalIdentity(new Probe { Signing = false }) is IdentityLost { Reason: "cannot-sign" }, "signing failure");
                Check(store.CheckLocalIdentity(new Probe { Throws = true }) is IdentityLost { Reason: "probe-failed" }, "probe exception fails closed");
                using var observer = Connect(path);
                long version = Number(observer, "PRAGMA data_version");
                Check(store.CheckLocalIdentity(probe) is IdentityConsistent && Number(observer, "PRAGMA data_version") == version,
                    "identity check has no write path");
                foreach (var state in Enum.GetValues<PairingState>())
                {
                    string id = "Peer_" + state;
                    var registered = store.RegisterPeer(id, state, new byte[] { 3 });
                    Check(registered is PeerRegistered { Created: true }, "explicit five-state peer registration");
                    Check(Text(path, $"SELECT pairingState FROM Peer WHERE id='{id}'") == PairingStates.ToWire(state), "state persisted without implicit pairing");
                    Check(store.TouchPeerSeen(id, "192.168.1.2").Written == (state == PairingState.Paired), "only paired peer can accept seen evidence");
                }
                Check(typeof(IChatStore).GetMethod(nameof(IChatStore.RegisterPeer))!.GetParameters()[1].HasDefaultValue == false,
                    "pairingState has no default parameter");
                var original = (PeerRegistered)store.RegisterPeer("Peer_A", PairingState.Revoked, new byte[] { 5 });
                Check(store.RegisterPeer("Peer_A", PairingState.Paired, new byte[] { 5 }) is PeerRegistered { Created: false } again &&
                    again.Conversation.ConversationId == original.Conversation.ConversationId && Text(path, "SELECT pairingState FROM Peer WHERE id='Peer_A'") == "revoked",
                    "re-register never silently restores revoked trust");
                Check(store.RegisterPeer("Peer_A", PairingState.Paired, new byte[] { 6 }) is PeerKeyMismatch &&
                    Text(path, "SELECT hex(publicKey) FROM Peer WHERE id='Peer_A'") == "05", "different key rejected without mutation");
                Constraint(path, "UPDATE Peer SET publicKey=X'06' WHERE id='Peer_A'", "pinned key immutable through raw SQL");
                store.RegisterPeer("peer_a", PairingState.Unpaired);
                Check(Number(path, "SELECT count(*) FROM Peer WHERE id IN ('Peer_A','peer_a')") == 2, "opaque ids case sensitive");
                Check(!store.TouchPeerSeen("Absent", "192.168.1.2").Written, "seen evidence never creates a peer");
                Execute(path, "UPDATE Peer SET pairingState='future-state' WHERE id='peer_a'");
                Check(store.TouchPeerSeen("peer_a", "192.168.1.2").Reason == "unknown-or-untrusted-peer" &&
                    store.GetDiagnostics().UnknownPairingStatesSeen == 1, "unknown persisted pairing state untrusted");
                string conv = original.Conversation.ConversationId;
                Constraint(path, $"UPDATE Conversation SET draftRevision=-1 WHERE id='{conv}'", "negative draft guard rejected");
                Execute(path, $"UPDATE Conversation SET draftRevision=5 WHERE id='{conv}'");
                Constraint(path, $"UPDATE Conversation SET draftRevision=4 WHERE id='{conv}'", "draft revision monotonic");
                Constraint(path, $"DELETE FROM Conversation WHERE id='{conv}'", "draft guard survives conversation clear");
                Constraint(path, "INSERT INTO Draft(conversationId,rawText,updatedUtc) VALUES('missing','x','t')", "foreign key enforcement");
                store.SetPairingState("Peer_A", PairingState.Paired);
                Check(Text(path, "SELECT pairingState FROM Peer WHERE id='Peer_A'") == "paired", "explicit state change");
                Check(store.Checkpoint(), "checkpoint without held reader succeeds");
            }
            using (var reopened = ChatStore.Open(new(area.Data)))
                Check(reopened.EnsureLocalIdentity() == "Local_A" && Number(reopened.DatabasePath, "SELECT count(*) FROM Peer") == 7,
                    "close/reopen retains identity, peers and conversations");
            Check(Hash(settings) == settingsHash, "settings file unchanged throughout store operations");
        }

        foreach (var parts in new[] { (Key: (byte[]?)null, Ref: (string?)null), (Key: (byte[]?)new byte[] { 1 }, Ref: (string?)null),
            (Key: (byte[]?)null, Ref: (string?)"ref") })
        {
            using var area = new Area(); using var store = ChatStore.Open(new(area.Data));
            Check(store.CheckLocalIdentity(new Probe()) is IdentityLost { Reason: "no-row" }, "missing identity row fails closed");
            store.EnsureLocalIdentity("id", parts.Key, parts.Ref);
            Check(parts.Key is null && parts.Ref is null ? store.CheckLocalIdentity(new Probe()) is IdentityNotProvisioned :
                store.CheckLocalIdentity(new Probe()) is IdentityLost { Reason: "incomplete-identity" }, "only both unset means not provisioned");
        }

        using (var area = new Area())
        {
            var faults = new Faults();
            using var store = ChatStore.Open(new(area.Data) { Faults = faults });
            Check(store.ReplaceLocalIdentity("new", null, null) is NoIdentityToReplace, "replacement never creates an absent identity");
            store.EnsureLocalIdentity("old", new byte[] { 1, 2 }, "old-ref");
            string conv = ((PeerRegistered)store.RegisterPeer("P", PairingState.Paired)).Conversation.ConversationId;
            string path = store.DatabasePath;
            foreach (string state in new[] { "sending", "unconfirmed", "failed", "cancelled", "confirmed" })
                Execute(path, $"INSERT INTO ManualSendAttempt(messageId,conversationId,senderId,recipientId,rawSnapshot,fingerprintAlg,contentFingerprint,draftEpoch,draftRevision,attemptState,createdUtc,updatedUtc) VALUES('{state}','{conv}','old','P','SYNTHETIC-TEXT','sha256-v1',zeroblob(32),0,0,'{state}','t','t')");
            Execute(path, $"INSERT INTO Message(conversationId,direction,senderId,recipientId,messageId,rawText,kind,fingerprintAlg,contentFingerprint,createdUtc) VALUES('{conv}','outgoing','old','P','confirmed','PRESERVED-OUTGOING','text','sha256-v1',zeroblob(32),'t'); INSERT INTO Message(conversationId,direction,senderId,recipientId,messageId,rawText,kind,fingerprintAlg,contentFingerprint,createdUtc) VALUES('{conv}','incoming','P','old','incoming','PRESERVED-INCOMING','text','sha256-v1',zeroblob(32),'t'); INSERT INTO ReceiptOutbox(originalLocalSeq,conversationId,recipientId,receiptKind,createdUtc) SELECT localSeq,'{conv}','P','read','t' FROM Message WHERE messageId='incoming'");
            faults.Before = op => { if (op == "replace-identity") throw new IOException("synthetic rollback"); };
            bool aborted = false;
            try { store.ReplaceLocalIdentity("new", new byte[] { 3 }, "new-ref"); } catch (IOException) { aborted = true; }
            faults.Before = null;
            Check(aborted && Text(path, "SELECT id FROM LocalIdentity") == "old" && Number(path, "SELECT count(*) FROM LocalIdentityHistory") == 0 &&
                Number(path, "SELECT count(*) FROM ManualSendAttempt WHERE attemptState='superseded'") == 0 && Number(path, "SELECT count(*) FROM ReceiptOutbox") == 1,
                "failed identity replacement rolls back identity, history, attempts and receipts together");
            Check(store.ReplaceLocalIdentity("new", new byte[] { 3 }, "new-ref") is IdentityReplaced { OldId: "old", NewId: "new", SupersededAttempts: 3, DroppedReceipts: 1 },
                "identity replacement atomically retires old sender and unsent receipts");
            Check(Number(path, "SELECT count(*) FROM Message") == 2 && Text(path, "SELECT senderId || ':' || rawText FROM Message WHERE messageId='confirmed'") == "old:PRESERVED-OUTGOING",
                "replacement preserves sender and original message history");
            Check(Text(path, "SELECT attemptState FROM ManualSendAttempt WHERE messageId='confirmed'") == "confirmed" &&
                Text(path, "SELECT attemptState FROM ManualSendAttempt WHERE messageId='cancelled'") == "cancelled", "confirmed and cancelled attempts unchanged");
            Check(store.ReplaceLocalIdentity("old", null, null) is IdentityReused && store.ReplaceLocalIdentity("new", null, null) is IdentityUnchanged,
                "retired identity reuse rejected and current identity idempotent");
            Constraint(path, "UPDATE LocalIdentityHistory SET reason='modified'", "retired history append only");
            Constraint(path, "DELETE FROM LocalIdentityHistory", "retired history cannot be removed");
            Constraint(path, "UPDATE LocalIdentity SET id='old'", "retired identity cannot return via raw SQL");
            Constraint(path, "UPDATE ManualSendAttempt SET senderId='new' WHERE messageId='sending'", "attempt sender immutable");
            Constraint(path, "UPDATE ManualSendAttempt SET attemptState='sending' WHERE messageId='unconfirmed'", "superseded attempt terminal");
            Constraint(path, "UPDATE ManualSendAttempt SET attemptState='superseded' WHERE messageId='confirmed'", "confirmed attempt terminal");
            Constraint(path, "UPDATE Message SET senderId='new' WHERE messageId='confirmed'", "stored sender immutable");
            Check(store.CheckLocalIdentity(new Probe { Key = new byte[] { 3 } }) is IdentityConsistent, "replacement public key matches its new credential");
        }

        using (var area = new Area())
        {
            string path = Path.Combine(area.Data, "nekochat.db");
            using (var store = ChatStore.Open(new(area.Data)))
            {
                store.EnsureLocalIdentity("old");
                string conv = ((PeerRegistered)store.RegisterPeer("P", PairingState.Paired)).Conversation.ConversationId;
                Execute(path, $"INSERT INTO ManualSendAttempt(messageId,conversationId,senderId,recipientId,rawSnapshot,fingerprintAlg,contentFingerprint,draftEpoch,draftRevision,attemptState,createdUtc,updatedUtc) VALUES('pending','{conv}','old','P','SYNTHETIC','sha256-v1',zeroblob(32),0,0,'sending','t','t')");
            }
            using var reopened = ChatStore.Open(new(area.Data));
            Check(Text(path, "SELECT attemptState FROM ManualSendAttempt") == "unconfirmed" && reopened.GetDiagnostics().RecoveredAttempts == 1 &&
                Number(path, "SELECT count(*) FROM Message") == 0, "startup recovery records uncertainty without creating message history or sending");
        }

        using (var area = new Area())
        {
            var faults = new Faults { After = op => { if (op == "ensure-identity") throw new IOException("synthetic post-commit exception"); } };
            using (var store = ChatStore.Open(new(area.Data) { Faults = faults }))
            {
                bool threw = false;
                try { store.EnsureLocalIdentity("committed"); } catch (IOException) { threw = true; }
                Check(threw && Text(store.DatabasePath, "SELECT id FROM LocalIdentity") == "committed", "exception after commit does not undo durable fact");
            }
            using var reopened = ChatStore.Open(new(area.Data));
            Check(reopened.EnsureLocalIdentity() == "committed", "post-commit exception survives reopen");
        }

        using (var area = new Area())
        {
            var clock = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            using var store = ChatStore.Open(new(area.Data) { UtcNow = () => clock });
            store.RegisterPeer("P", PairingState.Paired);
            Check(store.TouchPeerSeen("P", "10.0.0.1").Written, "first seen writes");
            clock = clock.AddMilliseconds(29_900);
            Check(store.TouchPeerSeen("P", "10.0.0.1").Reason == "throttled", "29.9 seconds throttled");
            clock = clock.AddMilliseconds(100);
            Check(store.TouchPeerSeen("P", "10.0.0.1").Written, "exactly 30 seconds writes");
            Check(store.TouchPeerSeen("P", "10.0.0.2") is { Written: true, EndpointChanged: true, PreviousEndpoint: "10.0.0.1" }, "endpoint change immediate");
            clock = clock.AddHours(-1);
            Check(store.TouchPeerSeen("P", "10.0.0.2").Written, "backward clock does not freeze lastSeen");
            store.SetPairingState("P", PairingState.Suspended);
            Check(!store.TouchPeerSeen("P", "10.0.0.3").Written && Text(store.DatabasePath, "SELECT lastSeenEndpoint FROM Peer WHERE id='P'") == "10.0.0.2",
                "suspended evidence cannot update endpoint");
        }

        using (var area = new Area())
        {
            using (var store = ChatStore.Open(new(area.Data) { Migrations = new[] { Migration.V1 } })) { store.EnsureLocalIdentity("old"); store.RegisterPeer("P", PairingState.Paired); }
            string path = Path.Combine(area.Data, "nekochat.db");
            string before = Hash(path);
            DateTime now = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            string backupDirectory = Path.Combine(area.Data, "backup"); Directory.CreateDirectory(backupDirectory);
            string collision = Path.Combine(backupDirectory, "nekochat-v1-20260102T030405Z.db"); File.WriteAllText(collision, "preserve-existing-backup");
            var migrations = new[] { Migration.V1, new Migration(2, ctx => { ctx.Exec("CREATE TABLE Partial(x INTEGER) STRICT"); throw new InvalidOperationException("synthetic failure"); }) };
            Error(() => ChatStore.Open(new(area.Data) { UtcNow = () => now, Migrations = migrations }).Dispose(), StoreErrorKind.MigrationFailed, "failed migration reported");
            Check(Number(path, "PRAGMA user_version") == 1 && Number(path, "SELECT count(*) FROM sqlite_master WHERE name='Partial'") == 0 &&
                Text(path, "SELECT id FROM LocalIdentity") == "old", "migration DDL and data rolled back");
            Check(Hash(path) == before, "failed migration retains original DB bytes");
            Check(File.ReadAllText(collision) == "preserve-existing-backup", "backup name collision never overwritten");
            string backup = Directory.GetFiles(backupDirectory).Single(p => p != collision);
            Check(Number(backup, "PRAGMA user_version") == 1 && Text(backup, "SELECT id FROM LocalIdentity") == "old", "online backup is independently readable");
        }

        using (var area = new Area())
        {
            using (var store = ChatStore.Open(new(area.Data))) store.EnsureLocalIdentity("old");
            string path = Path.Combine(area.Data, "nekochat.db");
            Execute(path, "PRAGMA user_version=99"); string before = Hash(path);
            Error(() => ChatStore.Open(new(area.Data)).Dispose(), StoreErrorKind.SchemaTooNew, "newer schema rejected");
            Check(Hash(path) == before && Number(path, "PRAGMA user_version") == 99, "newer schema unchanged");
        }
        using (var area = new Area())
        {
            Directory.CreateDirectory(area.Data); string path = Path.Combine(area.Data, "nekochat.db");
            Execute(path, "CREATE TABLE ForeignData(secret TEXT); INSERT INTO ForeignData VALUES('synthetic')", create: true);
            string before = Hash(path);
            Error(() => ChatStore.Open(new(area.Data)).Dispose(), StoreErrorKind.UnrecognizedDatabase, "foreign zero-version database rejected");
            Check(Hash(path) == before, "foreign database unchanged");
        }
        using (var area = new Area())
        {
            Directory.CreateDirectory(area.Data); string path = Path.Combine(area.Data, "nekochat.db"); File.WriteAllText(path, "not a database, synthetic");
            string before = Hash(path); Error(() => ChatStore.Open(new(area.Data)).Dispose(), StoreErrorKind.Corrupt, "corrupt file classified");
            Check(Hash(path) == before, "corrupt file preserved for recovery");
        }

        using (var area = new Area())
        {
            bool immediateLock = false;
            var faults = new Faults { Began = op => {
                if (op != "ensure-identity") return;
                using var other = Connect(Path.Combine(area.Data, "nekochat.db"));
                try { using var tx = other.BeginTransaction(deferred: false); }
                catch (SqliteException e) when (e.SqliteErrorCode == 5) { immediateLock = true; }
            }};
            using var store = ChatStore.Open(new(area.Data) { Faults = faults });
            store.EnsureLocalIdentity("id"); Check(immediateLock, "product write takes lock at BEGIN before any statement");
            using var fast = ChatStore.Open(new(area.Data) { CommandTimeoutSeconds = 1 });
            using (var other = Connect(store.DatabasePath))
            using (var tx = other.BeginTransaction(deferred: false))
            {
                // Default Timeout is configured on this store's connection, not a sleep or a replacement busy handler.
                var watch = Stopwatch.StartNew();
                Error(() => fast.EnsureLocalIdentity("blocked"), StoreErrorKind.StorageBusy, "held writer gives typed BUSY");
                Check(watch.ElapsedMilliseconds is >= 700 and < 6000 && Text(store.DatabasePath, "SELECT id FROM LocalIdentity") == "id",
                    "one-second lock timeout is bounded and does not overwrite identity");
            }
        }

        using (var area = new Area())
        {
            using var store = ChatStore.Open(new(area.Data));
            store.DebugScalar("PRAGMA query_only=ON");
            Error(() => store.EnsureLocalIdentity("blocked"), StoreErrorKind.ReadOnly, "SQLite read-only write error classified");
            store.DebugScalar("PRAGMA query_only=OFF");
            Check(Number(store.DatabasePath, "SELECT count(*) FROM LocalIdentity") == 0, "read-only failed write leaves no identity");
        }

        using (var area = new Area())
        {
            using (var store = ChatStore.Open(new(area.Data))) store.RegisterPeer("kept", PairingState.Unpaired);
            string path = Path.Combine(area.Data, "nekochat.db"); int pages = (int)Number(path, "PRAGMA page_count");
            using var limited = ChatStore.Open(new(area.Data) { MaxPageCount = pages + 1 });
            Error(() => limited.EnsureLocalIdentity("failed", new byte[256 * 1024], "SYNTHETIC-PRIVATE-MARKER"), StoreErrorKind.DiskFull, "real SQLite max_page_count triggers FULL");
            Check(Number(path, "SELECT count(*) FROM LocalIdentity") == 0 && Number(path, "SELECT count(*) FROM Peer") == 1, "FULL rolls back the entire new identity without losing prior commit");
        }

        // A forged version row must not make an unrelated DB eligible for persistent PRAGMAs/recovery writes.
        using (var area = new Area())
        {
            Directory.CreateDirectory(area.Data); string path = Path.Combine(area.Data, "nekochat.db");
            Execute(path, "PRAGMA secure_delete=OFF; PRAGMA journal_mode=WAL; CREATE TABLE SchemaVersion(version INTEGER); INSERT INTO SchemaVersion VALUES(1); PRAGMA user_version=1", create: true);
            string before = Hash(path);
            Error(() => ChatStore.Open(new(area.Data)).Dispose(), StoreErrorKind.UnrecognizedDatabase, "fake schema version does not identify a valid NekoChat DB");
            Check(Hash(path) == before, "forged database remains byte-for-byte unchanged");
        }

        using (var area = new Area())
        {
            Directory.CreateDirectory(area.Data); string path = Path.Combine(area.Data, "nekochat.db");
            using (var connection = Connect(path, create: true))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL;" + Migration.ReadSchemaV1().Replace("\n", "\r\n", StringComparison.Ordinal);
                command.Parameters.AddWithValue("@now", "2026-01-01T00:00:00.000Z"); command.Parameters.AddWithValue("@appVersion", "synthetic-windows-lines");
                command.ExecuteNonQuery();
            }
            using var store = ChatStore.Open(new(area.Data));
            Check(Number(path, "PRAGMA user_version") == 10, "recognized Windows CRLF v1 schema upgrades to v10");
        }
        using (var area = new Area())
        {
            Directory.CreateDirectory(area.Data); string protectedFile = Path.Combine(area.Data, "preserved.db"); File.WriteAllText(protectedFile, "synthetic preserved bytes");
            bool linked = false;
            try { Directory.CreateSymbolicLink(Path.Combine(area.Root, "Updates"), area.Data); linked = true; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
            if (linked)
            {
                bool refused = false;
                try { CacheCleaner.Clean(area.Root); } catch (IOException) { refused = true; }
                Check(refused && File.ReadAllText(protectedFile) == "synthetic preserved bytes", "cache root symlink cannot delete data");
            }
            else Console.WriteLine("Storage cache symlink check not executed: platform privilege unavailable.");
        }
        Check(((ICollection<string>)CacheCleaner.CleanableFolders).IsReadOnly, "cache deletion whitelist cannot be redirected by mutating its array");
        using (var area = new Area())
        {
            using (var store = ChatStore.Open(new(area.Data))) store.EnsureLocalIdentity("preserved");
            string database = Path.Combine(area.Data, "nekochat.db"), identity = Path.Combine(area.Root, "identity"), settings = Path.Combine(area.Root, "settings.json");
            Directory.CreateDirectory(identity); File.WriteAllText(Path.Combine(identity, "synthetic-public-test.bin"), "preserved-synthetic"); File.WriteAllText(settings, "preserved-settings");
            string before = Hash(database);
            Directory.CreateDirectory(Path.Combine(area.Root, "Updates", "nested")); Directory.CreateDirectory(Path.Combine(area.Root, "materials"));
            File.WriteAllText(Path.Combine(area.Root, "Updates", "nested", "cached.txt"), "cache"); File.WriteAllText(Path.Combine(area.Root, "materials", "cached.txt"), "cache");
            Check(CacheCleaner.Clean(area.Root) == 2, "normal cache contents removed");
            Check(Hash(database) == before && File.ReadAllText(settings) == "preserved-settings" && File.ReadAllText(Path.Combine(identity, "synthetic-public-test.bin")) == "preserved-synthetic",
                "cache cleanup retains DB, identity directory and settings");
            using var reopened = ChatStore.Open(new(area.Data)); Check(reopened.EnsureLocalIdentity() == "preserved", "database still opens after cache cleanup");
        }
        using (var area = new Area())
        {
            using var store = ChatStore.Open(new(area.Data) { NewId = () => "not-a-uuid" });
            bool refused = false;
            try { store.RegisterPeer("P", PairingState.Unpaired); } catch (InvalidOperationException) { refused = true; }
            Check(refused && Number(store.DatabasePath, "SELECT count(*) FROM Peer") == 0 && Number(store.DatabasePath, "SELECT count(*) FROM Conversation") == 0,
                "invalid generated conversation id rolls back peer creation");
        }
        Error(() => ChatStore.Open(new("\\\\synthetic-server\\share\\NekoData")).Dispose(), StoreErrorKind.DataLocationUnsupported, "UNC data location rejected before touching filesystem");
        Console.WriteLine($"Storage foundation: {checks} real SQLite checks passed.");
        return checks;
    }

    private static SqliteConnection Connect(string path, bool create = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1 }.ToString());
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON"; cmd.ExecuteNonQuery();
        return c;
    }
    private static void Execute(string path, string sql, bool create = false)
    {
        using var c = Connect(path, create); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    private static long Number(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static long Number(string path, string sql) { using var c = Connect(path); return Number(c, sql); }
    private static string Text(string path, string sql)
    {
        using var c = Connect(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class Area : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-store-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal Area() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Probe : IIdentityKeyProbe
    {
        public bool Exists = true, Readable = true, Signing = true, Throws;
        public byte[] Key = [1, 2];
        public bool KeyExists(string credentialRef) => Throws ? throw new IOException("synthetic probe failure") : Exists;
        public bool TryGetPublicKey(string credentialRef, out byte[] spki) { spki = Key; return Readable; }
        public bool CanSign(string credentialRef) => Signing;
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Began;
        internal Action<string>? Before;
        internal Action<string>? After;
        public void AfterBegin(string op) => Began?.Invoke(op);
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
