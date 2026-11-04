using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageHistoryChecks
{
    private const string Mode = "--storage-clear-child";
    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != Mode) return false;
        if (args.Length != 4 || args[3] is not ("before" or "after")) throw new ArgumentException("Invalid clear fixture.");
        var faults = new Faults();
        void Stop(string op)
        {
            if (op != "clear-conversation") return;
            Console.WriteLine("READY:" + args[3]); Console.Out.Flush();
            using var gate = new ManualResetEventSlim(); gate.Wait(TimeSpan.FromSeconds(15));
            throw new TimeoutException("Clear fixture not stopped.");
        }
        if (args[3] == "before") faults.Before = Stop; else faults.After = Stop;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults });
        store.ClearConversation(args[2]); return true;
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Storage history #{checks}: {name}"); }
        void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        using (var f = new Fixture())
        {
            string a, b; long last;
            using (var store = ChatStore.Open(f.Options))
            {
                (a, b) = Prepare(store);
                for (int i = 0; i < 120; i++)
                {
                    Check(store.ReceiveVerifiedMessage(new("A", "m" + i, "Local", "body" + i, "text", i == 3 ? "too-fast" : null)) is Stored, "120 real committed messages");
                    if (i % 10 == 0) store.ReceiveVerifiedMessage(new("B", "b" + i, "Local", "other" + i));
                }
                store.SaveDraft(a, "draft A", 0, 1); store.SaveDraft(b, "draft B", 0, 1);
                var first = store.ListMessages(a); var second = store.ListMessages(a, first.NextBeforeLocalSeq); var third = store.ListMessages(a, second.NextBeforeLocalSeq);
                Check(first.Items.Count == 50 && second.Items.Count == 50 && third.Items.Count == 20 && third.NextBeforeLocalSeq is null, "120 messages page 50/50/20");
                var all = third.Items.Concat(second.Items).Concat(first.Items).ToArray();
                Check(all.Select(m => m.MessageId).SequenceEqual(Enumerable.Range(0, 120).Select(i => "m" + i)) && all.Select(m => m.LocalSeq).Distinct().Count() == 120,
                    "keyset pages preserve all messages with no overlap and ascending local sequence");
                Check(all.All(m => m.ConversationId == a && m.SenderId == "A" && m.RecipientId == "Local" && m.LocalReadAt is null) && all[3].Reaction == "too-fast" &&
                    all[0].RawText == "body0" && all[0].FingerprintHex.Length == 64, "materialized full snapshots with untouched status and raw text");
                Check(store.GetUnreadCount(a) == 120 && store.GetUnreadCount(b) == 12 && store.GetDraftState(a)!.RawText == "draft A", "paging never changes read status or draft");
                store.ReceiveVerifiedMessage(new("A", "new", "Local", "arrived during paging"));
                Check(store.ListMessages(a, first.NextBeforeLocalSeq).Items.Select(m => m.LocalSeq).SequenceEqual(second.Items.Select(m => m.LocalSeq)), "new arrivals do not shift older keyset page");
                last = store.ListMessages(a).Items[^1].LocalSeq;
                Check(store.ListMessages(a, 1).Items.Count == 0 && store.ListMessages(Guid.NewGuid().ToString("D")).Items.Count == 0, "empty and nonexistent pages");
                Reject(() => store.ListMessages(a, 0), "nonpositive cursor rejected");
                Reject(() => store.ListMessages(a, null, 0), "empty page size rejected");
                Reject(() => store.ListMessages(a, null, 201), "oversized page rejected");
                Check(store.ListMessages(a, null, 200).Items.Count == 121 && store.ListMessages(a, null, 1).NextBeforeLocalSeq == last, "one and max page sizes");
                Check(first.Items.Count == 50 && first.Items[^1].MessageId == "m119", "returned page does not retain a live database reader");
            }
            using var reopened = ChatStore.Open(f.Options);
            Check(reopened.ListMessages(a).Items[^1].LocalSeq == last && reopened.GetDraftState(a)!.RawText == "draft A" && reopened.GetDraftState(b)!.RawText == "draft B",
                "history and independent drafts survive reopen");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, b) = Prepare(store);
            const string marker = "CLEAR-UNIQUE-cc047ea48a2d4b8eb03c2fc55d640830";
            var seed = SeedClear(store, f.Db, a, b, marker);
            byte[] settings = File.ReadAllBytes(f.Settings); byte[] key = File.ReadAllBytes(f.Key);
            var result = store.ClearConversation(a);
            Check(result is { Messages: 3, Drafts: 1, Attempts: 2, Receipts: 1, WalTruncated: true, NewDraftEpoch: 1 }, "one transaction clears four related tables and advances epoch");
            Check(Count(f.Db, "Message", a) == 0 && Count(f.Db, "Draft", a) == 0 && Count(f.Db, "ManualSendAttempt", a) == 0 && Count(f.Db, "ReceiptOutbox", a) == 0,
                "direct SQLite sees no conversation content");
            Check(store.GetDraftState(a) is { Epoch: 1, Revision: 9, RawText: null } && store.ListMessages(b).Items.Count == 1 && store.GetDraftState(b)!.RawText == "other draft",
                "other conversation and revision high-water retained");
            Check(Number(f.Db, "SELECT count(*) FROM LocalIdentity") == 1 && Number(f.Db, "SELECT count(*) FROM Peer") == 2 && Number(f.Db, "SELECT count(*) FROM Conversation") == 2 &&
                Number(f.Db, "SELECT count(*) FROM RosterEntry") == 1 && settings.SequenceEqual(File.ReadAllBytes(f.Settings)) && key.SequenceEqual(File.ReadAllBytes(f.Key)), "identity, roster, peer, settings and keys untouched");
            foreach (int revision in new[] { 10, 15, 509 }) Check(!store.SaveDraft(a, marker, 0, revision).Saved, "all older generation saves rejected even above high-water");
            Check(store.CompleteAttempt(seed.Attempt, Ack(marker)) is Discarded { Reason: "not-found" } &&
                store.ApplyRemoteReceipt("A", seed.Confirmed, RemoteReceiptKind.Read, "Local").Reason == "not-found" &&
                store.BeginManualRetry(seed.Attempt, marker, a, 1, 10) is NothingToRetry && store.ListMessages(a).Items.Count == 0, "late ACK/read/retry never resurrect cleared rows");
            Check(!Contains(f.Db, marker) && !Contains(f.Db + "-wal", marker), "secure-delete and successful truncate remove unique text from current db and wal");
            Check(store.SaveDraft(a, "new edit", 1, 10).Saved, "fresh edit after clear uses returned epoch");
            Check(store.ReceiveVerifiedMessage(new("A", "incoming", "Local", marker)) is ClearedDuplicate && store.GetUnreadCount(a) == 0,
                "explicit peer resend cannot resurrect a cleared key");
            var resent = (Stored)store.ReceiveVerifiedMessage(new("A", "new-after-clear", "Local", "new message"));
            Check(resent.LocalSeq > seed.MaxSeq && store.GetUnreadCount(a) == 1, "new key after clear gets a fresh non-reused local sequence");
            Check(store.ClearConversation(a).NewDraftEpoch == 2 && store.ClearConversation(a).NewDraftEpoch == 3, "repeated human clear advances epoch even when empty");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); var (a, b) = Prepare(store); SeedClear(store, f.Db, a, b, "rollback marker");
            faults.Before = op => { if (op == "clear-conversation") throw new IOException("injected before commit"); };
            bool failed = false; try { store.ClearConversation(a); } catch (IOException) { failed = true; }
            faults.Before = null;
            Check(failed && Count(f.Db, "Message", a) == 3 && Count(f.Db, "Draft", a) == 1 && Count(f.Db, "ManualSendAttempt", a) == 2 &&
                Count(f.Db, "ReceiptOutbox", a) == 1 && Number(f.Db, "SELECT count(*) FROM ClearedMessage") == 0 && store.GetDraftState(a)!.Epoch == 0, "clear failure rolls back all content, tombstones and guards");
            Execute(f.Db, $"UPDATE Conversation SET draftEpoch={long.MaxValue} WHERE id='{a}'");
            Reject(() => store.ClearConversation(a), "clear cannot wrap epoch");
            Check(Count(f.Db, "Message", a) == 3, "epoch exhaustion preserves all content");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, b) = Prepare(store); SeedClear(store, f.Db, a, b, "reader marker");
            using (var reader = Connect(f.Db))
            using (var tx = reader.BeginTransaction(deferred: true))
            {
                using var cmd = reader.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT count(*) FROM Message"; _ = cmd.ExecuteScalar();
                var result = store.ClearConversation(a);
                Check(!result.WalTruncated && store.GetDiagnostics().LastCheckpointBusy && store.ListMessages(a).Items.Count == 0,
                    "long reader truthfully reports checkpoint busy while logical clear is committed");
            }
            Check(store.Checkpoint() && !store.GetDiagnostics().LastCheckpointBusy, "truncate retries successfully after reader releases snapshot");
        }
        int children = 0;
        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); string a;
            using (var setup = ChatStore.Open(f.Options)) { var pair = Prepare(setup); a = pair.A; SeedClear(setup, f.Db, a, pair.B, "kill marker"); }
            using var child = StartChild(f.Data, a, stage); children++;
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "READY:" + stage, "clear child reaches product commit boundary");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            await Task.Delay(100);
            using var recovered = ChatStore.Open(f.Options); bool after = stage == "after";
            Check(Count(f.Db, "Message", a) == (after ? 0 : 3) && Count(f.Db, "ManualSendAttempt", a) == (after ? 0 : 2) &&
                Count(f.Db, "ReceiptOutbox", a) == (after ? 0 : 1) && recovered.GetDraftState(a) is { Revision: 9 } state && state.Epoch == (after ? 1 : 0) &&
                (state.RawText is null) == after, "kill leaves all four tables and epoch either entirely cleared or entirely preserved");
            Check(Number(f.Db, "SELECT count(*) FROM ClearedMessage") == (after ? 4 : 0) &&
                (recovered.ReceiveVerifiedMessage(new("A", "incoming", "Local", "kill marker")) is ClearedDuplicate) == after,
                "clear tombstones share crash atomicity and survive reopen");
        }
        Check(children == 2, "two bounded clear children");
        Console.WriteLine($"Storage history: {checks} paging, clear, rollback and checkpoint checks passed; 2 killed children."); return checks;
    }

    private static (string A, string B) Prepare(ChatStore store)
    {
        store.EnsureLocalIdentity("Local"); return (((PeerRegistered)store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId,
            ((PeerRegistered)store.RegisterPeer("B", PairingState.Paired)).Conversation.ConversationId);
    }
    private static (string Attempt, string Confirmed, long MaxSeq) SeedClear(ChatStore store, string db, string a, string b, string marker)
    {
        var incoming = (Stored)store.ReceiveVerifiedMessage(new("A", "incoming", "Local", marker));
        store.ReceiveVerifiedMessage(new("A", "incoming2", "Local", marker));
        store.ReceiveVerifiedMessage(new("B", "other", "Local", "other body")); store.SaveDraft(b, "other draft", 0, 1);
        var confirmed = (AttemptStarted)store.BeginManualSend(a, marker, 0, 1);
        store.SaveDraft(a, marker, 0, 2); store.CompleteAttempt(confirmed.MessageId, Ack(marker));
        var pending = (AttemptStarted)store.BeginManualSend(a, marker, 0, 9);
        Execute(db, $"INSERT INTO ReceiptOutbox VALUES({incoming.LocalSeq},'{a}','A','read','2026-01-01'); INSERT INTO RosterEntry(id,displayName,isTeacher) VALUES('r','roster',0)");
        return (pending.MessageId, confirmed.MessageId, Number(db, "SELECT max(localSeq) FROM Message"));
    }
    private static RemoteStoredOutcome Ack(string body) => new(DateTime.MinValue, SHA256.HashData(Encoding.UTF8.GetBytes("nekochat-v2-content\nA\n\n" + body)));
    private static Process StartChild(string data, string conversation, string stage)
    {
        string executable = Environment.ProcessPath!; var psi = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(StorageHistoryChecks).Assembly.Location);
        foreach (var arg in new[] { Mode, data, conversation, stage }) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start clear fixture.");
    }
    private static SqliteConnection Connect(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA foreign_keys=ON";
                cmd.ExecuteNonQuery();
                return c;
            }
            catch (SqliteException ex) when (attempt < 4 && (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 10))
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        var finalConn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
        finalConn.Open();
        using var finalCmd = finalConn.CreateCommand();
        finalCmd.CommandText = "PRAGMA foreign_keys=ON";
        finalCmd.ExecuteNonQuery();
        return finalConn;
    }
    private static long Number(string path, string sql) { using var c = Connect(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private static void Execute(string path, string sql) { using var c = Connect(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static long Count(string path, string table, string conv) => Number(path, $"SELECT count(*) FROM {table} WHERE conversationId='{conv}'");
    private static bool Contains(string path, string marker)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-history-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data"); internal string Db => Path.Combine(Data, "nekochat.db");
        internal string Settings => Path.Combine(Root, "settings.json"); internal string Key => Path.Combine(Root, "identity", "synthetic.key");
        internal ChatStoreOptions Options => new(Data) { CommandTimeoutSeconds = 1 };
        internal Fixture() { Directory.CreateDirectory(Path.GetDirectoryName(Key)!); File.WriteAllText(Key, "synthetic only"); File.WriteAllText(Settings, "{\"keep\":true}"); }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
