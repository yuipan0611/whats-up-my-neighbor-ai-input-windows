using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class LegacyMessageChecks
{
    private const string ChildMode = "--legacy-message-child";
    public static async Task<bool> RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 3 || args[2] is not ("before" or "after")) throw new ArgumentException("Invalid legacy child arguments.");
        var faults = new Faults();
        void Stop(string op)
        {
            if (op != "legacy-receive") return;
            Console.WriteLine("BOUNDARY:" + args[2]); Console.Out.Flush(); using var wait = new ManualResetEventSlim(false); wait.Wait(TimeSpan.FromSeconds(15));
            throw new TimeoutException("Parent did not kill legacy fixture.");
        }
        if (args[2] == "before") faults.Before = Stop; else faults.After = Stop;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults });
        await using var listener = new DirectLanListener(null, Options(), store); listener.Start();
        Console.WriteLine("PORT:" + listener.Port); Console.Out.Flush();
        _ = await Console.In.ReadLineAsync(); return true;
    }
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Legacy message: " + name); }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            var expected = store.GetLegacyReceiveContext("10.0.0.1");
            Check(expected is { RosterEntryId: null, RosterRevision: null, ClearRevision: 0 }, "legacy can receive without provisioning a local identity or peer");
            var first = store.ReceiveLegacyMessage(new("raw\r\n🐈", "future-sticker"), expected, () => true);
            var second = store.ReceiveLegacyMessage(new("raw\r\n🐈", "future-sticker"), expected, () => true);
            Check(first.Stored && second.Stored && first.LegacySeq != second.LegacySeq && Number(f.Db, "SELECT count(*) FROM LegacyMessage") == 2, "legacy has no fabricated wire id or deduplication");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 0 && Number(f.Db, "SELECT count(*) FROM Peer") == 0 && Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0,
                "legacy never creates trusted identity, verified history or receipts");
            var page = store.ListLegacyMessages(expected.Scope);
            Check(page.Items.All(m => m.TrustLabel == "舊版・未驗證" && m.SourceIp == "10.0.0.1" && m.RawText == "raw\r\n🐈" && m.Reaction == "future-sticker" && m.LocalReadAt is null), "raw text and unknown reaction preserved in explicitly unverified records");
            var history = store.ListLegacyHistories().Single();
            Check(history is { Messages: 2, Unread: 2 } && history.Scope == expected.Scope && history.LastLegacySeq == second.LegacySeq, "separate immutable inbox summaries include local unread only");
            Check(store.ListLegacyMessages(new(null, "10.0.0.2")).Items.Count == 0, "source scopes isolated");
            Check(!store.ReceiveLegacyMessage(new(new string('a', 2001), null), expected, () => true).Stored &&
                !store.ReceiveLegacyMessage(new("\ud800", null), expected, () => true).Stored, "legacy storage rejects scalar overflow and malformed Unicode");
            Check(store.ReceiveLegacyMessage(new("\0x", null), expected, () => true).Stored && store.ListLegacyMessages(expected.Scope).Items[^1].RawText == "\0x",
                "legacy preserves original v1 control-character semantics without SQLite NUL length truncation");
        }
        using (var f = new Fixture())
        {
            string row;
            using (var old = ChatStore.Open(f.Options with { Migrations = Migration.Default.Take(5).ToArray() })) { row = AddRow(old, "Alice", "10.0.0.1"); }
            using var current = ChatStore.Open(f.Options);
            Check(Number(f.Db, "PRAGMA user_version") == 6 && Number(f.Db, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT GLOB 'sqlite_*'") == 18 &&
                current.GetRosterSnapshot().Single().Entry.Id == row, "real v5 upgrade adds two STRICT legacy tables without changing roster");
            string backup = Path.Combine(f.Data, "backup", current.ListBackups().Single(b => b.Purpose == "upgrade").FileName);
            Check(Number(backup, "PRAGMA user_version") == 5, "upgrade preimage remains standalone true v5");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); string alice = AddRow(store, "Alice", "10.0.0.1");
            var captured = store.GetLegacyReceiveContext("10.0.0.1"); store.ReceiveLegacyMessage(new("Alice arrival", null), captured, () => true);
            EditRow(store, alice, "Alice", "10.0.0.2"); string bob = AddRow(store, "Bob", "10.0.0.1");
            var bobContext = store.GetLegacyReceiveContext("10.0.0.1");
            Check(captured.RosterEntryId == alice && bobContext.RosterEntryId == bob && !store.ReceiveLegacyMessage(new("stale Alice callback", null), captured, () => true).Stored,
                "IP reuse rejects old roster callback and captures the new row");
            store.ReceiveLegacyMessage(new("Bob arrival", null), bobContext, () => true);
            Check(store.ListLegacyMessages(new(alice, null)).Items.Single().RawText == "Alice arrival" && store.ListLegacyMessages(new(bob, null)).Items.Single().RawText == "Bob arrival",
                "existing legacy record never moves to another person when IP reused");
            store.ClearLegacyHistory(new(alice, null));
            Check(store.ReceiveLegacyMessage(new("Bob still current", null), bobContext, () => true).Stored && store.ListLegacyMessages(new(bob, null)).Items.Count == 2,
                "clearing prior row does not invalidate new row sharing its former IP");
            store.DeleteRosterEntry(bob, Token(store)); string carol = AddRow(store, "Carol", "10.0.0.1");
            Check(store.ListLegacyMessages(new(null, "10.0.0.1")).Items.Count == 2 && store.ListLegacyMessages(new(carol, null)).Items.Count == 0,
                "deleting roster row makes old records anonymous rather than assigning replacement row");
            Check(store.ListLegacyHistories().Single().Scope == new LegacyHistoryScope(null, "10.0.0.1"), "roster deletion changes display scope without promoting trust");
            var afterDelete = store.ClearLegacyHistory(new(null, "10.0.0.1")); Check(afterDelete.Messages == 2 && store.ListLegacyHistories().Count == 0, "anonymous clear also covers retained arrival scope keys");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); string row = AddRow(store, "Alice", "10.0.0.1"); var captured = store.GetLegacyReceiveContext("10.0.0.1");
            EditRow(store, row, "Alice", "10.0.0.2"); EditRow(store, row, "Alice", "10.0.0.1");
            Check(!store.ReceiveLegacyMessage(new("old route", null), captured, () => true).Stored, "durable roster revision rejects endpoint A-B-A");
            captured = store.GetLegacyReceiveContext("10.0.0.1"); EditRow(store, row, "Renamed", "10.0.0.1");
            Check(store.ReceiveLegacyMessage(new("renamed row", null), captured, () => true).Stored, "display-name change keeps the same row mapping");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var captured = store.GetLegacyReceiveContext("10.0.0.1");
            store.ClearLegacyHistory(captured.Scope);
            Check(!store.ReceiveLegacyMessage(new("queued before empty clear", null), captured, () => true).Stored && store.ListLegacyHistories().Count == 0,
                "clear of empty scope fences previously captured callback");
            var current = store.GetLegacyReceiveContext("10.0.0.1"); var first = store.ReceiveLegacyMessage(new("new", null), current, () => true);
            store.ClearLegacyHistory(current.Scope); var second = store.ReceiveLegacyMessage(new("new arrival after clear", null), store.GetLegacyReceiveContext("10.0.0.1"), () => true);
            Check(first.Stored && second.Stored && second.LegacySeq > first.LegacySeq && store.ListLegacyMessages(current.Scope).Items.Count == 1, "fresh unidentifiable arrival remains new, sequence never reused");
            bool refused = false; try { Execute(f.Db, "DELETE FROM LegacyScopeRevision"); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { refused = true; }
            Check(refused, "clear guards cannot be deleted");
            refused = false; try { Execute(f.Db, "UPDATE LegacyScopeRevision SET revision=0"); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { refused = true; }
            Check(refused, "clear guards cannot decrease");
        }
        foreach (string stage in new[] { "begin", "commit" })
        {
            using var f = new Fixture(); bool live = true; using var store = ChatStore.Open(f.Options);
            var captured = store.GetLegacyReceiveContext("10.0.0.1");
            if (stage == "begin") f.Faults.Begin = op => { if (op == "legacy-receive") live = false; };
            else f.Faults.Before = op => { if (op == "legacy-receive") live = false; };
            Check(!store.ReceiveLegacyMessage(new("not committed", null), captured, () => live).Stored && Number(f.Db, "SELECT count(*) FROM LegacyMessage") == 0 && Number(f.Db, "SELECT count(*) FROM LegacyScopeRevision") == 0,
                stage + " cancellation leaves no message or first-scope metadata");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var scope = new LegacyHistoryScope(null, "10.0.0.1"); var captured = store.GetLegacyReceiveContext("10.0.0.1");
            var seqs = Enumerable.Range(0, 3).Select(i => store.ReceiveLegacyMessage(new("read" + i, null), captured, () => true).LegacySeq!.Value).ToArray();
            store.SetReadReceiptPolicy(true);
            var visible = new VisibleReadContext(scope.ContextId, true, true, false, true);
            foreach (var invalid in new[] { visible with { UserOpened = false }, visible with { WindowActive = false }, visible with { Minimized = true }, visible with { ViewportReady = false }, visible with { ActiveConversationId = "other" } })
                Check(store.MarkLegacyVisibleRead(scope, new[] { seqs[0] }, invalid, () => true).Marked == 0, "legacy requires real local visible context " + checks);
            Check(store.MarkLegacyVisibleRead(scope, new[] { seqs[0], seqs[0] }, visible, () => true) is { Marked: 1, Queued: 0 } && store.ListLegacyHistories().Single().Unread == 2 && Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0,
                "legacy locally reads exact visible row but never queues receipt even policyON");
            bool live = true; f.Faults.Before = op => { if (op == "legacy-visible-read") live = false; };
            Check(store.MarkLegacyVisibleRead(scope, new[] { seqs[1] }, visible, () => live).Reason == "stale-view" && store.ListLegacyHistories().Single().Unread == 2, "final view cancellation rolls back local legacy read");
            f.Faults.Before = null;
            var other = store.GetLegacyReceiveContext("10.0.0.2"); var otherSeq = store.ReceiveLegacyMessage(new("other", null), other, () => true).LegacySeq!.Value;
            Check(store.MarkLegacyVisibleRead(scope, new[] { otherSeq }, visible, () => true).Marked == 0 && Number(f.Db, "SELECT count(*) FROM LegacyMessage WHERE localReadAt IS NOT NULL") == 1, "legacy visible read cannot mark another scope");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); store.EnsureLocalIdentity("Local");
            string conv = ((PeerRegistered)store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId;
            string row = AddRow(store, "Alice", "10.0.0.1"); store.BindPairedRosterPeer(row, "A", Token(store));
            var captured = store.GetLegacyReceiveContext("10.0.0.1"); store.ReceiveLegacyMessage(new("legacy", null), captured, () => true);
            store.ReceiveVerifiedMessage(new("A", "signed", "Local", "verified")); store.SaveDraft(conv, "keep until clear commits", 0, 1);
            var other = store.GetLegacyReceiveContext("10.0.0.2"); store.ReceiveLegacyMessage(new("other", null), other, () => true);
            f.Faults.Before = op => { if (op == "clear-conversation") throw new IOException("injected clear failure"); };
            bool failed = false; try { store.ClearConversation(conv); } catch (IOException) { failed = true; } f.Faults.Before = null;
            Check(failed && store.ListMessages(conv).Items.Count == 1 && store.ListLegacyMessages(captured.Scope).Items.Count == 1 && store.GetDraftState(conv)?.RawText is not null && store.GetLegacyReceiveContext("10.0.0.1") == captured,
                "combined verified/legacy clear rollback preserves all tables and clear revision");
            var clear = store.ClearConversation(conv);
            Check(clear is { Messages: 1, LegacyMessages: 1, Drafts: 1 } && store.ListMessages(conv).Items.Count == 0 && store.ListLegacyMessages(captured.Scope).Items.Count == 0 &&
                store.ListLegacyMessages(other.Scope).Items.Count == 1 && !store.ReceiveLegacyMessage(new("stale", null), captured, () => true).Stored, "verified clear atomically includes bound roster legacy and fences queued old callback, other scope intact");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var context = store.GetLegacyReceiveContext("10.0.0.1"); store.ReceiveLegacyMessage(new("preserved", null), context, () => true);
            Execute(f.Db, "UPDATE LegacyScopeRevision SET revision=" + long.MaxValue);
            StoreException? error = null; try { store.ClearLegacyHistory(context.Scope); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.ConstraintViolation && store.ListLegacyMessages(context.Scope).Items.Count == 1, "revision overflow rejects clear atomically instead of wrap or deleting content");
        }
        using (var f = new Fixture())
        {
            using var first = ChatStore.Open(f.Options); using var second = ChatStore.Open(f.Options); var context = first.GetLegacyReceiveContext("10.0.0.1"); using var gate = new ManualResetEventSlim();
            var tasks = new[] { first, second }.Select(s => Task.Factory.StartNew(() => { if (!gate.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException(); return s.ReceiveLegacyMessage(new("same unidentifiable arrival", null), context, () => true); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            gate.Set(); var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(12));
            Check(results.All(r => r.Stored) && first.ListLegacyMessages(context.Scope).Items.Count == 2, "two handles deliberately retain both unidentifiable v1 arrivals");
            for (int i = 0; i < 119; i++) first.ReceiveLegacyMessage(new("page" + i, null), context, () => true);
            var page1 = first.ListLegacyMessages(context.Scope); var page2 = first.ListLegacyMessages(context.Scope, page1.NextBeforeLegacySeq); var page3 = first.ListLegacyMessages(context.Scope, page2.NextBeforeLegacySeq);
            Check(page1.Items.Count == 50 && page2.Items.Count == 50 && page3.Items.Count == 21 && page3.NextBeforeLegacySeq is null && page3.Items[^1].LegacySeq < page2.Items[0].LegacySeq, "legacy121 messages page50/50/21 with stable sequence cursor");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); await using var listener = new DirectLanListener(null, Options(), store); int notices = 0; listener.MessageReceived += (_, _) => notices++; listener.Start();
            Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "persist through facade", listener.Port) && store.ListLegacyHistories().Single().Unread == 1 && notices == 1 && Number(f.Db, "SELECT count(*) FROM Message") == 0, "real legacy TCP facade persists before event/courtesy reply without trusted history");
            f.Faults.Before = op => { if (op == "legacy-receive") throw new IOException("private body"); };
            bool failed = false; try { await DirectLanMessaging.SendDirectAsync("127.0.0.1", "cannot save", listener.Port); } catch (InvalidOperationException) { failed = true; } f.Faults.Before = null;
            Check(failed && store.ListLegacyHistories().Single().Messages == 1 && notices == 1, "failed local persistence gives no delivered reply or receive event");
        }
        using (var f = new Fixture())
        {
            using (var store = ChatStore.Open(f.Options)) { }
            Execute(f.Db, "CREATE TABLE unexpected(x)"); string before = Hash(f.Db); StoreException? error = null;
            try { using var invalid = ChatStore.Open(f.Options); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.UnrecognizedDatabase && Hash(f.Db) == before, "v6 schema drift refused read-only before any write");
        }
        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); using var child = StartChild(f.Data, stage); var errors = child.StandardError.ReadToEndAsync();
            try
            {
                string ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) ?? ""; Check(ready.StartsWith("PORT:", StringComparison.Ordinal), "legacy child opens real product listener");
                Task<bool> sending = DirectLanMessaging.SendDirectAsync("127.0.0.1", "legacy crash", int.Parse(ready[5..]));
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "BOUNDARY:" + stage, "actual TCP legacy receive reaches exact commit boundary " + stage);
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                bool noAck = false; try { _ = await sending; } catch (InvalidOperationException) { noAck = true; }
                int expected = stage == "after" ? 1 : 0;
                Check(child.ExitCode != 0 && noAck && Number(f.Db, "SELECT count(*) FROM LegacyMessage") == expected && Number(f.Db, "SELECT count(*) FROM Message") == 0, "true killed legacy transaction native DB proves0/1 with no false courtesy ACK");
                using var reopened = ChatStore.Open(f.Options); await using var listener = new DirectLanListener(null, Options(), reopened); listener.Start();
                Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "legacy crash", listener.Port) && reopened.ListLegacyHistories().Single().Messages == expected + 1, "fresh v1 retry is another arrival because protocol has no id or reliable dedup");
                Check((await errors).Length == 0 && Number(f.Db, "SELECT count(*) FROM ReceiptOutbox") == 0, "legacy crash/retry produces no read receipt or private diagnostics");
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
        }
        Console.WriteLine($"Legacy message persistence: {checks} separate history, mapping, clear, local read and actual TCP/crash checks;2 killed children."); return checks;
    }
    private static string Token(ChatStore store) => RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
    private static string AddRow(ChatStore store, string name, string ip) => store.SaveManualRosterEntry(null, new(name, null, false, ip), Token(store)).EntryId!;
    private static void EditRow(ChatStore store, string row, string name, string ip) { if (!store.SaveManualRosterEntry(row, new(name, null, false, ip), Token(store)).Changed) throw new Exception("fixture roster edit"); }
    private static V2ListenerOptions Options() => new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 1000 };
    private static Process StartChild(string data, string stage)
    {
        string executable = Environment.ProcessPath!; var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(LegacyMessageChecks).Assembly.Location);
        info.ArgumentList.Add(ChildMode); info.ArgumentList.Add(data); info.ArgumentList.Add(stage); return Process.Start(info)!;
    }
    private static object? Query(string path, string sql)
    { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    private static long Number(string path, string sql) => Convert.ToInt64(Query(path, sql));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Execute(string path, string sql)
    { using var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-legacy-msg-" + Guid.NewGuid().ToString("N"));
        internal string Db => Path.Combine(Data, "nekochat.db");
        internal Faults Faults { get; } = new();
        internal ChatStoreOptions Options => new(Data) { Faults = Faults };
        internal Fixture() => Directory.CreateDirectory(Data);
        public void Dispose() => Directory.Delete(Data, true);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Begin, Before, After;
        public void AfterBegin(string op) => Begin?.Invoke(op);
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
