using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageReadChecks
{
    private const string Mode = "--storage-read-child";
    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != Mode) return false;
        if (args.Length != 5 || args[4] is not ("before" or "after")) throw new ArgumentException("Invalid read fixture.");
        var faults = new Faults();
        void Stop(string op)
        {
            if (op != "visible-read") return; Console.WriteLine("READY:" + args[4]); Console.Out.Flush();
            using var gate = new ManualResetEventSlim(); gate.Wait(TimeSpan.FromSeconds(15)); throw new TimeoutException("Read fixture not stopped.");
        }
        if (args[4] == "before") faults.Before = Stop; else faults.After = Stop;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults });
        store.MarkVisibleRead(args[2], [long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)], Active(args[2])); return true;
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Storage read #{checks}: {name}"); }
        Check(!ReadVisibility.IsVisible(100, 49.99, 20, 500) && ReadVisibility.IsVisible(100, 50, 20, 500), "half message boundary");
        Check(!ReadVisibility.IsVisible(1000, 199.99, 20, 400) && ReadVisibility.IsVisible(1000, 200, 20, 400), "tall message uses half viewport");
        Check(!ReadVisibility.IsVisible(10, 5, 20, 400) && ReadVisibility.IsVisible(10, 10, 20, 400), "one complete rendered line required");
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1d, 0d }) Check(!ReadVisibility.IsVisible(bad, 1, 20, 400), "non-finite or invalid geometry rejected");
        Check(!ReadVisibility.IsVisible(100, 101, 20, 400) && !ReadVisibility.IsVisible(100, 50, 0, 400), "impossible intersections and invalid line size rejected");
        using (var f = new Fixture())
        {
            using (var old = ChatStore.Open(f.Options with { Migrations = new[] { Migration.V1, Migration.V2 } }))
            {
                var p = Prepare(old);
                Execute(f.Db, $"UPDATE Message SET localReadAt='2026-01-01T00:00:00.000Z' WHERE localSeq={p.M6}; INSERT INTO ReceiptOutbox VALUES({p.M6},'{p.A}','A','read','2026-01-01T00:00:00.000Z')");
            }
            using var upgraded = ChatStore.Open(f.Options);
            Check(upgraded.GetReadReceiptPolicy() is { Enabled: true, Generation: 1 } && Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0 &&
                Number(f.Db, "SELECT count(*) FROM Message WHERE localReadAt IS NOT NULL") == 1 && Number(f.Db, "SELECT count(*) FROM Message") == 3,
                "D-28 v2 upgrade enables unset receipts and drops old unsent queue while preserving local read/history");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, b, m6, m7, m8) = Prepare(store);
            Check(store.GetReadReceiptPolicy() is { Enabled: true, Generation: 1 } && store.ListPendingReadReceipts().Count == 0, "D-28 new privacy policy defaults on with no backfill");
            store.SetReadReceiptPolicy(false); // Explicit opt-out phase: preserves all off/read/re-enable guards under D-28.
            var active = Active(a);
            foreach (var denied in new[] { active with { UserOpened = false }, active with { WindowActive = false }, active with { Minimized = true },
                active with { ViewportReady = false }, active with { ActiveConversationId = b } })
                Check(store.MarkVisibleRead(a, [m6], denied) is { Marked: 0 } && store.GetUnreadCount(a) == 2, "each foreground/open/viewport/conversation guard rejects");
            var read = store.MarkVisibleRead(a, [m6, m6, m8, long.MaxValue], active);
            Check(read is { Marked: 1, Queued: 0 } && store.GetUnreadCount(a) == 1 && store.GetUnreadCount(b) == 1, "only visible same-conversation m6 read, no default outbound receipt");
            Check(store.SetReadReceiptPolicy(true).Policy is { Enabled: true, Generation: 3 } && store.ListPendingReadReceipts().Count == 0 &&
                store.MarkVisibleRead(a, [m6], active).Marked == 0, "enabling does not backfill reads made while off");
            Check(store.MarkVisibleRead(a, [m7], active) is { Marked: 1, Queued: 1 } && store.ListPendingReadReceipts().Count == 1, "allowed read and receipt queued together");
            var token = store.ListPendingReadReceipts().Single();
            Check(token is { ReaderId: "Local", OriginalSenderId: "A", MessageId: "m7", PolicyGeneration: 3 }, "metadata token binds reader/original sender/message/policy");
            Check(!store.CompleteReadReceipt(token with { OriginalSenderId = "B" }) && !store.CompleteReadReceipt(token with { MessageId = "unknown" }) &&
                !store.CompleteReadReceipt(token with { ReaderId = "other" }) && store.ListPendingReadReceipts().Count == 1, "mismatched receipt ACK cannot drop pending row");
            using var secondHandle = ChatStore.Open(f.Options);
            Check(secondHandle.SetReadReceiptPolicy(false) is { DroppedReceipts: 1, Policy.Generation: 4 } && store.GetReadReceiptPolicy().Enabled == false &&
                store.GetUnreadCount(a) == 0 && store.ListPendingReadReceipts().Count == 0, "disable is shared across handles, drops pending receipts, preserves local read");
            store.SetReadReceiptPolicy(true);
            Check(!store.CompleteReadReceipt(token) && store.ListPendingReadReceipts().Count == 0, "old generation ACK harmless after re-enable and no backfill");
            store.MarkVisibleRead(b, [m8], Active(b)); var receiptB = store.ListPendingReadReceipts().Single();
            Check(store.CompleteReadReceipt(receiptB) && !store.CompleteReadReceipt(receiptB) && store.ListPendingReadReceipts().Count == 0, "only verified ACK completion removes receipt; duplicate is idempotent");
            var session = new VisibleReadSession(store); long generation = session.Update(active); session.Update(Active(b));
            Check(session.Mark(a, generation, [m6]).Reason == "stale-view", "queued callback from previous view rejected by owned generation");
            var next = (Stored)store.ReceiveVerifiedMessage(new("A", "later", "Local", "later"));
            generation = session.Update(active); Check(session.Mark(a, generation, [next.LocalSeq]) is { Marked: 1, Queued: 1 }, "current view generation can mark read");
            store.SetPairingState("A", PairingState.Revoked);
            Check(store.ListPendingReadReceipts().Count == 0, "revoked peer cannot prepare an outbound read receipt");
            store.SetPairingState("A", PairingState.Paired); store.ReplaceLocalIdentity("NewLocal", null, null);
            Check(store.ListPendingReadReceipts().Count == 0 && Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0, "identity replacement drops old-reader receipts");
        }
        using (var f = new Fixture())
        {
            string a; long m7;
            using (var store = ChatStore.Open(f.Options))
            {
                var p = Prepare(store); a = p.A; m7 = p.M7; store.SetReadReceiptPolicy(true); store.MarkVisibleRead(a, [m7], Active(a));
            }
            using var reopened = ChatStore.Open(f.Options);
            Check(reopened.GetReadReceiptPolicy() is { Enabled: true, Generation: 1 } && reopened.ListPendingReadReceipts().Single().MessageId == "m7",
                "allowed pending receipt and preference survive reopen without sending");
            reopened.SetReadReceiptPolicy(false);
            var read = reopened.MarkVisibleRead(a, [((Stored)reopened.ReceiveVerifiedMessage(new("A", "off", "Local", "off"))).LocalSeq], Active(a));
            Check(read is { Marked: 1, Queued: 0 }, "new reads remain local after disable");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); var (a, _, m6, _, _) = Prepare(store); store.SetReadReceiptPolicy(true);
            faults.Before = op => { if (op == "visible-read") throw new IOException("injected after read and queue"); };
            bool failed = false; try { store.MarkVisibleRead(a, [m6], Active(a)); } catch (IOException) { failed = true; } faults.Before = null;
            Check(failed && store.GetUnreadCount(a) == 2 && store.ListPendingReadReceipts().Count == 0, "read and outbox roll back together");
            store.MarkVisibleRead(a, [m6], Active(a));
            faults.Before = op => { if (op == "read-policy") throw new IOException("injected disable rollback"); };
            failed = false; try { store.SetReadReceiptPolicy(false); } catch (IOException) { failed = true; } faults.Before = null;
            Check(failed && store.GetReadReceiptPolicy() is { Enabled: true, Generation: 1 } && store.ListPendingReadReceipts().Count == 1, "disable and queue deletion roll back together");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); var p = Prepare(store); store.SetReadReceiptPolicy(true);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            faults.Begun = op => { if (op == "visible-read") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Read gate timed out."); } };
            var session = new VisibleReadSession(store); long generation = session.Update(Active(p.A));
            var work = Task.Factory.StartNew(() => session.Mark(p.A, generation, [p.M6]), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Read did not enter transaction.");
                session.Update(Active(p.B));
            }
            finally { release.Set(); }
            var result = await work.WaitAsync(TimeSpan.FromSeconds(10)); faults.Begun = null;
            Check(result.Reason == "stale-view" && store.GetUnreadCount(p.A) == 2 && store.ListPendingReadReceipts().Count == 0,
                "focus change stays responsive while DB waits; view generation rechecked inside transaction before mutation");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var p = Prepare(store); var send = (AttemptStarted)store.BeginManualSend(p.A, "out", 0, 1);
            Check(store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Read, "Local").Reason == "pending-local-confirmation" &&
                Number(f.Db, "SELECT count(*) FROM Message WHERE direction='outgoing'") == 0, "early read receipt is retryable without inventing an outgoing message");
            store.ClearConversation(p.A);
            Check(store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Read, "Local").Reason == "not-found", "cleared attempt is discarded, not kept pending forever");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var p = Prepare(store); store.SetReadReceiptPolicy(true); store.MarkVisibleRead(p.A, [p.M6], Active(p.A));
            Execute(f.Db, $"UPDATE ReadReceiptPolicy SET generation={long.MaxValue}");
            Check(store.SetReadReceiptPolicy(false) is { Policy.Enabled: false, Policy.Generation: long.MaxValue, DroppedReceipts: 1 }, "exhausted counter still permits fail-closed disable");
            bool exhausted = false; try { store.SetReadReceiptPolicy(true); } catch (InvalidOperationException) { exhausted = true; }
            Check(exhausted && !store.GetReadReceiptPolicy().Enabled, "exhausted generation cannot re-enable or wrap and revive old tokens");
        }
        int children = 0;
        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); string a; long m6;
            using (var setup = ChatStore.Open(f.Options)) { var p = Prepare(setup); a = p.A; m6 = p.M6; setup.SetReadReceiptPolicy(true); }
            using var child = StartChild(f.Data, a, m6, stage); children++;
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "READY:" + stage, "read child reaches commit boundary");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            await Task.Delay(100);
            using var recovered = ChatStore.Open(f.Options); bool after = stage == "after";
            Check(recovered.GetUnreadCount(a) == (after ? 1 : 2) && recovered.ListPendingReadReceipts().Count == (after ? 1 : 0), "real kill preserves read/outbox atomicity");
        }
        Check(children == 2, "exactly two bounded read children");
        Console.WriteLine($"Storage read: {checks} visibility, privacy, receipt and crash checks passed; 2 killed children."); return checks;
    }

    private static VisibleReadContext Active(string id) => new(id, true, true, false, true);
    private static (string A, string B, long M6, long M7, long M8) Prepare(ChatStore store)
    {
        store.EnsureLocalIdentity("Local"); string a = ((PeerRegistered)store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId;
        string b = ((PeerRegistered)store.RegisterPeer("B", PairingState.Paired)).Conversation.ConversationId;
        return (a, b, ((Stored)store.ReceiveVerifiedMessage(new("A", "m6", "Local", "six"))).LocalSeq,
            ((Stored)store.ReceiveVerifiedMessage(new("A", "m7", "Local", "seven"))).LocalSeq, ((Stored)store.ReceiveVerifiedMessage(new("B", "m8", "Local", "eight"))).LocalSeq);
    }
    private static long Number(string db, string sql)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
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
        using var finalConn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False");
        finalConn.Open();
        using var finalCmd = finalConn.CreateCommand();
        finalCmd.CommandText = sql;
        return Convert.ToInt64(finalCmd.ExecuteScalar());
    }
    private static void Execute(string db, string sql)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var c = new SqliteConnection("Data Source=" + db + ";Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
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
        using var finalConn = new SqliteConnection("Data Source=" + db + ";Pooling=False");
        finalConn.Open();
        using var finalCmd = finalConn.CreateCommand();
        finalCmd.CommandText = sql;
        finalCmd.ExecuteNonQuery();
    }
    private static Process StartChild(string data, string conversation, long seq, string stage)
    {
        string exe = Environment.ProcessPath!; var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(StorageReadChecks).Assembly.Location);
        foreach (var arg in new[] { Mode, data, conversation, seq.ToString(System.Globalization.CultureInfo.InvariantCulture), stage }) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException("Read child failed.");
    }
    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-read-" + Guid.NewGuid().ToString("N")); internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data);
        internal Fixture() => Directory.CreateDirectory(Data);
        public void Dispose() => Directory.Delete(Data, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After, Begun;
        public void AfterBegin(string op) => Begun?.Invoke(op);
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
