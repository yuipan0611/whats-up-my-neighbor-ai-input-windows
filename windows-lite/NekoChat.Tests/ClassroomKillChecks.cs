using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;

/// <summary>
/// Final-acceptance kill tests (classroom + shared path): a CHILD PROCESS runs a real ChatStore on real SQLite and is stopped with SIGKILL
/// (Process.Kill on Unix = kill -9, TerminateProcess on Windows) exactly at a chosen point inside a store write; the parent then reopens the
/// store and checks what survived. Kill points come from the store's own internal IStoreFaults seam (AfterBegin / BeforeCommit / AfterCommit of a
/// named write); the child prints KILLPOINT and blocks, so the kill is delivered from outside while the write is frozen at that point.
/// The TCP scenarios run the real listener + sink, and kill the child either right after the client received the ACK or while the request is frozen.
/// Nothing here edits product code; it uses only the public IChatStore surface plus the existing internal fault seam.
///   RECV   receive: nothing before commit, exactly one row after, a resend is never stored twice
///   TCP    ACK received =&gt; the row is already durable; killed before commit =&gt; no ACK and no row; killed after commit => no ACK, resend answers duplicate
///   SEND   begin / route / complete: a kill never loses the text, never confirms without commit, reopen makes `sending` unconfirmed, retry reuses the id
///   RCPT   visible-read writes the outbox with the read mark; complete-receipt keeps the outbox until commit; policy-off drops it; remote read mark
///   CLEAR  all-or-nothing; after the clear a late resend recreates nothing (messages, unread, outbox)
/// </summary>
internal static class ClassroomKillChecks
{
    private const string X = "10.0.0.1";
    private static readonly DateTime Clock = new(2026, 11, 3, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string[] BeforePhases = ["AfterBegin", "BeforeCommit"];

    // ---------------------------------------------------------------------------------------------------------------- scenarios

    private sealed record Scenario(string Name, string Op, Action<Env> Seed, Action<Env> Act, Action<Env, Checker> Before, Action<Env, Checker> After);

    private sealed class Env
    {
        internal required ChatStore Store { get; init; }
        internal required string Db { get; init; }
        internal ClassroomPrincipalKey Pa => ClassroomPrincipalKey.ForRosterEntry(Text("SELECT id FROM RosterEntry LIMIT 1"));
        internal long Count(string sql) => ClassroomKillChecks.Count(Db, sql);
        internal string Text(string sql) => ClassroomKillChecks.Text(Db, sql);
        internal string AttemptId() => Text("SELECT messageId FROM ClassroomAttempt LIMIT 1");
        internal byte[] Fingerprint(string id) => Convert.FromHexString(Store.GetClassroomPreparedSend(id)!.FingerprintHex);
    }

    private static readonly Scenario[] Scenarios =
    [
        new("RECV classroom-receive", "classroom-receive",
            Seed: e => AddRoster(e.Store),
            Act: e => e.Store.ReceiveClassroomMessage(X, new("rcv-1", "你好 kill"), () => true),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage"), 0, "nothing stored before the commit");
                c.That(e.Store.ReceiveClassroomMessage(X, new("rcv-1", "你好 kill"), () => true) is ClassroomStored { UnreadCount: 1 }, "the resend of the same id is stored (it was never acknowledged)");
                c.That(e.Store.ReceiveClassroomMessage(X, new("rcv-1", "你好 kill"), () => true) is ClassroomDuplicate, "and the next resend is a duplicate");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage"), 1, "exactly one row after resends");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE messageId='rcv-1' AND rawText='你好 kill' AND direction='incoming'"), 1, "the committed message survived the kill");
                c.That(e.Store.ReceiveClassroomMessage(X, new("rcv-1", "你好 kill"), () => true) is ClassroomDuplicate, "a resend after the kill is a duplicate, not a second copy");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage"), 1, "still exactly one row");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE localReadAt IS NULL"), 1, "unread count is one");
            }),

        new("SEND classroom-begin-send", "classroom-begin-send",
            Seed: e => { AddRoster(e.Store); e.Store.SaveClassroomDraft(e.Pa, "hello x", 0, 1); },
            Act: e => e.Store.BeginClassroomSend(e.Pa, "hello x", 0, 1),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomAttempt"), 0, "no attempt before the commit");
                c.Is(e.Store.GetClassroomDraftState(e.Pa).RawText, "hello x", "the draft is kept");
                c.That(e.Store.BeginClassroomSend(e.Pa, "hello x", 0, 1) is AttemptStarted { TryCount: 1 }, "the send can begin again");
            },
            After: (e, c) =>
            {
                string id = e.AttemptId();
                c.Is(e.Text("SELECT attemptState FROM ClassroomAttempt WHERE messageId='" + id + "'"), "unconfirmed", "reopening turned the half-sent attempt into unconfirmed");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing'"), 0, "no outgoing message without a confirmed ACK");
                c.Is(e.Store.GetClassroomDraftState(e.Pa).RawText, "hello x", "the draft text is not lost");
                c.That(e.Store.GetClassroomPreparedSend(id) is null, "nothing is prepared for automatic sending");
                c.That(e.Store.BeginClassroomRetry(id, e.Pa, "hello x", 0, 1) is AttemptStarted { ReusedId: true, TryCount: 2 } s && s.MessageId == id, "a manual retry reuses the same message id");
            }),

        new("SEND classroom-record-route", "classroom-record-route",
            Seed: e => { AddRoster(e.Store); e.Store.SaveClassroomDraft(e.Pa, "hello x", 0, 1); },   // the attempt must be begun INSIDE the child: reopening a store turns `sending` into unconfirmed
            Act: e => { var b = (AttemptStarted)e.Store.BeginClassroomSend(e.Pa, "hello x", 0, 1); e.Store.RecordClassroomRoute(b.MessageId, 1, ClassroomRoute.Classroom, Clock); },
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomAttemptRoute"), 0, "no route recorded before the commit");
                c.Is(e.Text("SELECT attemptState FROM ClassroomAttempt"), "unconfirmed", "the attempt is unconfirmed after reopen");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing'"), 0, "nothing confirmed");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomAttemptRoute"), 1, "the committed route survived");
                c.Is(e.Text("SELECT attemptState FROM ClassroomAttempt"), "unconfirmed", "the attempt is unconfirmed after reopen");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing'"), 0, "a route record alone never creates an outgoing message");
                c.Is(e.Store.GetClassroomDraftState(e.Pa).RawText, "hello x", "the draft is kept");
            }),

        new("SEND classroom-complete-attempt", "classroom-complete-attempt",
            Seed: e => { AddRoster(e.Store); e.Store.SaveClassroomDraft(e.Pa, "hello x", 0, 1); },
            Act: e => { var b = (AttemptStarted)e.Store.BeginClassroomSend(e.Pa, "hello x", 0, 1); e.Store.RecordClassroomRoute(b.MessageId, 1, ClassroomRoute.Classroom, Clock); e.Store.CompleteClassroomAttempt(b.MessageId, new RemoteStoredOutcome(Clock, e.Fingerprint(b.MessageId)), X); },
            Before: (e, c) =>
            {
                string id = e.AttemptId();
                c.Is(e.Text("SELECT attemptState FROM ClassroomAttempt WHERE messageId='" + id + "'"), "unconfirmed", "killed before the commit: the attempt is unconfirmed, never confirmed");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing'"), 0, "no outgoing message");
                c.Is(e.Store.GetClassroomDraftState(e.Pa).RawText, "hello x", "the draft text is not lost");
                var retry = e.Store.BeginClassroomRetry(id, e.Pa, "hello x", 0, 1) as AttemptStarted;
                c.That(retry is { ReusedId: true, TryCount: 2 }, "retry keeps the id");
                c.That(retry is not null && e.Store.RecordClassroomRoute(id, 2, ClassroomRoute.Classroom, Clock).Recorded, "the retry can record its route");
                c.That(retry is not null && e.Store.CompleteClassroomAttempt(id, new RemoteStoredOutcome(Clock, e.Fingerprint(id)), X) is Confirmed, "the retry confirms");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing' AND messageId='" + id + "'"), 1, "exactly one outgoing row after the retry");
            },
            After: (e, c) =>
            {
                string id = e.AttemptId();
                c.Is(e.Text("SELECT attemptState FROM ClassroomAttempt WHERE messageId='" + id + "'"), "confirmed", "the committed confirmation survived and is not turned into unconfirmed");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing' AND messageId='" + id + "'"), 1, "exactly one outgoing message");
                c.That(e.Store.GetClassroomDraftState(e.Pa) is { RawText: null, Epoch: 1 }, "the unchanged draft was cleared and the epoch moved in the same commit");
                c.That(e.Store.CompleteClassroomAttempt(id, new RemoteStoredOutcome(Clock, ChatStore.ClassroomFingerprint("hello x", null)), X) is AlreadyConfirmed, "a second completion is AlreadyConfirmed");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE direction='outgoing'"), 1, "still one outgoing message");
            }),

        new("RCPT classroom-visible-read", "classroom-visible-read",
            Seed: e => { AddRoster(e.Store); e.Store.SetReadReceiptPolicy(true); e.Store.ReceiveClassroomMessage(X, new("rcv-1", "to read"), () => true); },
            Act: e => e.Store.MarkClassroomVisibleRead(e.Pa, [Seq(e)], Visible(e.Pa)),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE localReadAt IS NOT NULL"), 0, "not marked read before the commit");
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "no outbox row without the read mark");
                c.That(e.Store.MarkClassroomVisibleRead(e.Pa, [Seq(e)], Visible(e.Pa)) is { Marked: 1, Queued: 1 }, "reading again marks and queues once");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE localReadAt IS NOT NULL"), 1, "the read mark survived");
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 1, "the outbox row was written in the same commit as the read mark");
                c.That(e.Store.ListPendingClassroomReceipts() is [{ MessageId: "rcv-1", TargetIp: X }], "the pending receipt is listed with the conversation's address");
                e.Store.MarkClassroomVisibleRead(e.Pa, [Seq(e)], Visible(e.Pa));
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 1, "reading again never queues a second receipt");
            }),

        new("RCPT classroom-complete-receipt", "classroom-complete-receipt",
            Seed: e => { AddRoster(e.Store); e.Store.SetReadReceiptPolicy(true); e.Store.ReceiveClassroomMessage(X, new("rcv-1", "to read"), () => true); e.Store.MarkClassroomVisibleRead(e.Pa, [Seq(e)], Visible(e.Pa)); },
            Act: e => e.Store.CompleteClassroomReadReceipt(e.Store.ListPendingClassroomReceipts()[0]),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 1, "killed before the delete commits: the outbox event is kept (CM-13)");
                var pending = e.Store.ListPendingClassroomReceipts();
                c.That(pending.Count == 1 && e.Store.CompleteClassroomReadReceipt(pending[0]), "it is still deliverable and completes once");
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "and is gone afterwards");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "the completed receipt stays deleted after the kill");
                c.That(e.Store.ListPendingClassroomReceipts().Count == 0, "it is not listed again");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE localReadAt IS NOT NULL"), 1, "the local read mark is untouched");
            }),

        new("RCPT read-policy off", "read-policy",
            Seed: e => { AddRoster(e.Store); e.Store.SetReadReceiptPolicy(true); e.Store.ReceiveClassroomMessage(X, new("rcv-1", "to read"), () => true); e.Store.MarkClassroomVisibleRead(e.Pa, [Seq(e)], Visible(e.Pa)); },
            Act: e => e.Store.SetReadReceiptPolicy(false),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 1, "policy change not committed: the outbox event is kept");
                c.That(e.Store.GetReadReceiptPolicy().Enabled, "the policy is still on");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "turning receipts off drops every pending receipt in the same commit");
                c.That(!e.Store.GetReadReceiptPolicy().Enabled, "the policy is off");
                e.Store.SetReadReceiptPolicy(true);
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "turning it back on does not send the old receipts");
            }),

        new("RCPT classroom-apply-receipt (remote read mark)", "classroom-apply-receipt",
            Seed: e => { AddRoster(e.Store); e.Store.SaveClassroomDraft(e.Pa, "hello x", 0, 1); var b = (AttemptStarted)e.Store.BeginClassroomSend(e.Pa, "hello x", 0, 1); e.Store.RecordClassroomRoute(b.MessageId, 1, ClassroomRoute.Classroom, Clock); e.Store.CompleteClassroomAttempt(b.MessageId, new RemoteStoredOutcome(Clock, e.Fingerprint(b.MessageId)), X); },
            Act: e => e.Store.ApplyClassroomReceipt(X, e.Text("SELECT messageId FROM ClassroomMessage WHERE direction='outgoing'"), RemoteReceiptKind.Read),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE remoteReadAt IS NOT NULL"), 0, "no remote read mark before the commit");
                c.That(e.Store.ApplyClassroomReceipt(X, e.Text("SELECT messageId FROM ClassroomMessage WHERE direction='outgoing'"), RemoteReceiptKind.Read).Changed, "the resent receipt is applied");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage WHERE remoteReadAt IS NOT NULL"), 1, "the remote read mark survived");
                c.That(!e.Store.ApplyClassroomReceipt(X, e.Text("SELECT messageId FROM ClassroomMessage WHERE direction='outgoing'"), RemoteReceiptKind.Read).Changed, "a resent receipt changes nothing (duplicate)");
            }),

        new("CLEAR classroom-clear", "classroom-clear",
            Seed: e =>
            {
                AddRoster(e.Store); e.Store.SetReadReceiptPolicy(true);
                e.Store.ReceiveClassroomMessage(X, new("rcv-1", "first"), () => true); e.Store.ReceiveClassroomMessage(X, new("rcv-2", "second"), () => true);
                e.Store.MarkClassroomVisibleRead(e.Pa, [Seq(e)], Visible(e.Pa));
                e.Store.SaveClassroomDraft(e.Pa, "hello x", 0, 1); var b = (AttemptStarted)e.Store.BeginClassroomSend(e.Pa, "hello x", 0, 1);
                e.Store.RecordClassroomRoute(b.MessageId, 1, ClassroomRoute.Classroom, Clock); e.Store.CompleteClassroomAttempt(b.MessageId, new RemoteStoredOutcome(Clock, e.Fingerprint(b.MessageId)), X);
                var d = e.Store.GetClassroomDraftState(e.Pa); e.Store.SaveClassroomDraft(e.Pa, "pending draft", d.Epoch, d.Revision + 1);
            },
            Act: e => e.Store.ClearClassroomConversation(e.Pa),
            Before: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage"), 3, "killed before the clear commits: every message is still there");
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 1, "the outbox event is still there");
                c.Is(e.Store.GetClassroomDraftState(e.Pa).RawText, "pending draft", "the draft is still there");
                c.Is(e.Count("SELECT count(*) FROM ClassroomCleared"), 0, "no half-written cleared markers");
                c.That(e.Store.ClearClassroomConversation(e.Pa) is { Messages: 3, Receipts: 1 }, "the clear can simply be run again");
            },
            After: (e, c) =>
            {
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage"), 0, "the clear removed every message");
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "the clear removed the pending receipts");
                c.Is(e.Count("SELECT count(*) FROM ClassroomAttempt"), 0, "the clear removed the attempts");
                c.Is(e.Count("SELECT count(*) FROM ClassroomDraft"), 0, "the clear removed the draft");
                c.That(e.Count("SELECT count(*) FROM ClassroomCleared") >= 3, "the cleared ids were remembered in the same commit");
                c.That(e.Store.ReceiveClassroomMessage(X, new("rcv-1", "first"), () => true) is ClassroomClearedDuplicate, "a late resend of a cleared message is recognised");
                c.That(e.Store.ReceiveClassroomMessage(X, new("rcv-2", "second"), () => true) is ClassroomClearedDuplicate, "so is the other one");
                c.Is(e.Count("SELECT count(*) FROM ClassroomMessage"), 0, "nothing was recreated");
                c.Is(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox"), 0, "no receipt was recreated");
                c.That(e.Store.ReceiveClassroomMessage(X, new("rcv-3", "new"), () => true) is ClassroomStored { UnreadCount: 1 }, "a genuinely new message is still stored");
            }),
    ];

    private static void AddRoster(ChatStore s)
    {
        var r = s.SaveManualRosterEntry(null, new("甲", null, false, X), RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot()));
        if (!r.Changed) throw new InvalidOperationException("kill fixture: roster " + r.Reason);
    }
    private static VisibleReadContext Visible(ClassroomPrincipalKey p) => new(p.Value, true, true, false, true);
    private static long Seq(Env e) => long.Parse(e.Text("SELECT min(localSeq) FROM ClassroomMessage WHERE direction='incoming'"));

    // ---------------------------------------------------------------------------------------------------------------- child side

    /// <summary>`--classroom-kill-child store &lt;scenario index&gt; &lt;dir&gt; &lt;phase|none&gt;` or `tcp &lt;dir&gt; &lt;phase|none&gt;`. Returns true when the arguments were a child command.</summary>
    internal static bool RunChild(string[] args)
    {
        if (args.Length < 4 || args[0] != "--classroom-kill-child") return false;
        if (args[1] == "store")
        {
            var scenario = Scenarios[int.Parse(args[2])]; string dir = args[3]; string phase = args[4];
            using var store = ChatStore.Open(Options(dir, phase == "none" ? null : new KillFaults(scenario.Op, phase)));
            scenario.Act(new Env { Store = store, Db = Path.Combine(dir, "nekochat.db") });
            Console.Out.WriteLine("DONE");
            return true;
        }
        if (args[1] == "tcp")
        {
            string dir = args[2]; string phase = args[3];
            using var store = ChatStore.Open(Options(dir, phase == "none" ? null : new KillFaults("classroom-receive", phase)));
            var adapter = new ClassroomEndpointAdapter(new ClassroomStoreSink(store)); adapter.SetReady(true);
            var listener = new ClassroomTcpListener(adapter, new ClassroomListenerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            listener.Start();
            Console.Out.WriteLine("PORT " + listener.Port);
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        return false;
    }

    private static ChatStoreOptions Options(string dir, IStoreFaults? faults) => new(dir) { UtcNow = () => Clock, Faults = faults };

    private sealed class KillFaults(string op, string phase) : IStoreFaults
    {
        private void At(string p, string o) { if (p == phase && o == op) { Console.Out.WriteLine("KILLPOINT"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); } }
        public void AfterBegin(string o) => At("AfterBegin", o);
        public void BeforeCommit(string o) => At("BeforeCommit", o);
        public void AfterCommit(string o) => At("AfterCommit", o);
    }

    // ---------------------------------------------------------------------------------------------------------------- parent side

    internal sealed class Checker
    {
        private readonly string _scope; private readonly List<string> _failures;
        internal int Count { get; private set; }
        internal Checker(string scope, List<string> failures) { _scope = scope; _failures = failures; }
        internal void That(bool ok, string label, string detail = "") { Count++; if (!ok) _failures.Add($"FAIL: [{_scope}] {label}{(detail.Length > 0 ? " — " + detail : "")}"); }
        internal void Is<T>(T actual, T expected, string label) => That(EqualityComparer<T>.Default.Equals(actual, expected), label, $"expected={expected} actual={actual}");
    }

    private sealed class Child : IDisposable
    {
        private readonly Process _p; private readonly System.Collections.Concurrent.BlockingCollection<string> _lines = new();
        internal string Stderr { get; private set; } = "";
        private Child(Process p) { _p = p; }
        internal static Child Start(params string[] args)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet") psi.ArgumentList.Add(typeof(ClassroomKillChecks).Assembly.Location);
            psi.ArgumentList.Add("--classroom-kill-child"); foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!; var child = new Child(p);
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) child._lines.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) child.Stderr += e.Data + "\n"; };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return child;
        }
        internal string? Next(int ms = 30_000) => _lines.TryTake(out var l, ms) ? l : null;
        internal bool HasExited => _p.HasExited;
        /// <summary>kill -9: SIGKILL on Unix. The child gets no chance to run handlers, flush or dispose.</summary>
        internal void Kill9() { try { _p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } _p.WaitForExit(10_000); }
        public void Dispose() { Kill9(); _p.Dispose(); _lines.Dispose(); }
    }

    internal static async Task<int> RunAsync()
    {
        var failures = new List<string>(); int total = 0; int killed = 0;
        string root = Path.Combine(Path.GetTempPath(), "neko-kill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < Scenarios.Length; i++)
            {
                var sc = Scenarios[i];
                foreach (string phase in new[] { "AfterBegin", "BeforeCommit", "AfterCommit", "none" })
                {
                    string scope = $"{sc.Name} / {(phase == "none" ? "no kill (control)" : "kill -9 at " + phase)}";
                    string dir = Path.Combine(root, $"s{i}-{phase}"); Directory.CreateDirectory(dir);
                    var c = new Checker(scope, failures);
                    try { using var seedStore = ChatStore.Open(Options(dir, null)); sc.Seed(new Env { Store = seedStore, Db = Path.Combine(dir, "nekochat.db") }); }
                    catch (Exception e) when (e is not OutOfMemoryException) { c.That(false, "the scenario could be seeded", e.GetType().Name + ": " + e.Message); total += c.Count; continue; }
                    using (var child = Child.Start("store", i.ToString(), dir, phase))
                    {
                        string? line = child.Next();
                        if (phase == "none") { c.That(line == "DONE", "the operation ran to the end", "child said " + (line ?? "<nothing>") + " " + child.Stderr); }
                        else if (line == "KILLPOINT") { child.Kill9(); killed++; }
                        else { c.That(false, "the kill point was reached (otherwise this test proves nothing)", "child said " + (line ?? "<nothing>") + " " + child.Stderr); total += c.Count; continue; }
                    }
                    using var store = ChatStore.Open(Options(dir, null));
                    var env = new Env { Store = store, Db = Path.Combine(dir, "nekochat.db") };
                    c.That(Text(env.Db, "PRAGMA integrity_check") == "ok", "SQLite integrity_check is ok after the kill");
                    try { (BeforePhases.Contains(phase) ? sc.Before : sc.After)(env, c); }
                    catch (Exception e) when (e is not OutOfMemoryException) { c.That(false, "the checks ran to the end", e.GetType().Name + ": " + e.Message); }
                    total += c.Count;
                }
            }
            try { await TcpScenarios(root, failures, () => total++, () => killed++); }
            catch (Exception e) when (e is not OutOfMemoryException) { failures.Add("FAIL: [TCP] scenario threw " + e.GetType().Name + ": " + e.Message); }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }

        foreach (string f in failures) Console.Error.WriteLine(f);
        Console.WriteLine($"Classroom kill checks: {total + 0} assertions over {killed} real kill -9 runs; {failures.Count} failed.");
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} classroom kill check(s) failed.");
        return total;
    }

    private static async Task TcpScenarios(string root, List<string> failures, Action count, Action killedOne)
    {
        // T1: no fault. The client got the stored ACK; kill -9 the child at that instant: the row must already be on disk.
        {
            string dir = Path.Combine(root, "tcp-ack"); Directory.CreateDirectory(dir); var c = new Checker("TCP ACK received then kill -9", failures);
            using (var child = Child.Start("tcp", dir, "none"))
            {
                int port = ReadPort(child, c);
                var r = port > 0 ? await Post(port, "11111111-1111-4111-8111-111111111111", "真實 TCP 後強殺") : (0, "");
                c.That(r == (200, "stored"), "the client received the stored ACK", $"got {r}");
                child.Kill9(); killedOne();
            }
            using var store = ChatStore.Open(Options(dir, null)); var db = Path.Combine(dir, "nekochat.db");
            c.Is(Count(db, "SELECT count(*) FROM ClassroomMessage WHERE messageId='11111111-1111-4111-8111-111111111111' AND rawText='真實 TCP 後強殺'"), 1, "an ACK that reached the client means the row is already durable");
            c.Is(Text(db, "PRAGMA integrity_check"), "ok", "integrity_check after the kill");
            foreach (var _ in Enumerable.Range(0, c.Count)) count();
        }
        foreach (string phase in new[] { "BeforeCommit", "AfterCommit" })
        {
            string dir = Path.Combine(root, "tcp-" + phase); Directory.CreateDirectory(dir); var c = new Checker($"TCP kill -9 {phase} (request frozen)", failures); string db = Path.Combine(dir, "nekochat.db");
            using (var child = Child.Start("tcp", dir, phase))
            {
                int port = ReadPort(child, c); Task<(int, string)>? send = port > 0 ? Post(port, "22222222-2222-4222-8222-222222222222", "凍結中的訊息", 4000) : null;
                string? line = child.Next();
                if (line != "KILLPOINT") c.That(false, "the kill point was reached", "child said " + (line ?? "<nothing>") + " " + child.Stderr);
                else { child.Kill9(); killedOne(); }
                var r = send is null ? (-1, "") : await send;
                c.That(r.Item1 != 200, "the client did NOT receive a stored ACK when the child died before replying", $"got {r}");
            }
            using (var store = ChatStore.Open(Options(dir, null)))
            {
                long rows = Count(db, "SELECT count(*) FROM ClassroomMessage WHERE messageId='22222222-2222-4222-8222-222222222222'");
                if (phase == "BeforeCommit") c.Is(rows, 0, "killed before commit: nothing was stored, and no ACK was sent");
                else c.Is(rows, 1, "killed after commit (before the reply): the row survived");
                c.Is(Text(db, "PRAGMA integrity_check"), "ok", "integrity_check after the kill");
            }
            using (var child2 = Child.Start("tcp", dir, "none"))
            {
                int port = ReadPort(child2, c); var r = port > 0 ? await Post(port, "22222222-2222-4222-8222-222222222222", "凍結中的訊息") : (0, "");
                c.That(r == (200, phase == "BeforeCommit" ? "stored" : "duplicate"), "the sender's resend after the restart is answered " + (phase == "BeforeCommit" ? "stored" : "duplicate"), $"got {r}");
                child2.Kill9(); killedOne();
            }
            using (var store = ChatStore.Open(Options(dir, null))) c.Is(Count(db, "SELECT count(*) FROM ClassroomMessage WHERE messageId='22222222-2222-4222-8222-222222222222'"), 1, "exactly one copy after the resend");
            foreach (var _ in Enumerable.Range(0, c.Count)) count();
        }
    }

    private static int ReadPort(Child child, Checker c)
    {
        string? line = child.Next();
        if (line is not null && line.StartsWith("PORT ")) return int.Parse(line[5..]);
        c.That(false, "the TCP child started", "child said " + (line ?? "<nothing>") + " " + child.Stderr);
        return 0;
    }

    private static async Task<(int Status, string Ack)> Post(int port, string id, string text, int timeoutMs = 10_000)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        try
        {
            string hello = $"http://127.0.0.1:{port}/nekochat/classroom/v1/hello";
            using var h = new HttpRequestMessage(HttpMethod.Post, hello) { Content = new ByteArrayContent(ClassroomPacketWriter.HelloRequest(ClassroomPacketWriter.NewToken())) };
            h.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var hr = await http.SendAsync(h); using var hd = JsonDocument.Parse(await hr.Content.ReadAsByteArrayAsync());
            string session = hd.RootElement.GetProperty("session").GetString()!;
            using var m = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/nekochat/classroom/v1/message") { Content = new ByteArrayContent(ClassroomPacketWriter.MessageSend(id, session, "127.0.0.1", text, null)) };
            m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var mr = await http.SendAsync(m); using var md = JsonDocument.Parse(await mr.Content.ReadAsByteArrayAsync());
            return ((int)mr.StatusCode, md.RootElement.TryGetProperty("status", out var s) ? s.GetString()! : "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return (-1, e.GetType().Name); }
    }

    private static long Count(string path, string sql) { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private static string Text(string path, string sql) { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? ""; }
}
