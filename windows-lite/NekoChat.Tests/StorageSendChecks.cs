using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class StorageSendChecks
{
    private const string ChildMode = "--storage-send-child";
    public static bool RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 4 || args[3] is not ("before" or "after")) throw new ArgumentException("Invalid send fixture arguments.");
        var faults = new Faults();
        void Stop(string operation)
        {
            if (operation != "complete-attempt") return;
            Console.WriteLine("READY:" + args[3]); Console.Out.Flush();
            using var wait = new ManualResetEventSlim(false); wait.Wait(TimeSpan.FromSeconds(15));
            throw new TimeoutException("Parent did not stop send fixture.");
        }
        if (args[3] == "before") faults.Before = Stop; else faults.After = Stop;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults });
        store.CompleteAttempt(args[2], Ack("A", "X"));
        return true;
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string description) { checks++; if (!condition) throw new Exception($"Storage send #{checks}: {description}"); }
        void Reject(Action action, string description)
        {
            bool failed = false; try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { failed = true; }
            Check(failed, description);
        }
        using (var f = new Fixture())
        {
            string a;
            using (var store = ChatStore.Open(f.Options))
            {
                (a, _) = Prepare(store);
                Check(store.GetDraftState(a) is { RawText: null, Epoch: 0, Revision: 0 }, "empty conversation has durable draft guards");
                Check(store.SaveDraft(a, "X", 0, 5).Saved, "draft revision advances");
                var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 5);
                Check(Number(f.Db, "SELECT count(*) FROM Message") == 0 && store.GetPreparedSend(send.MessageId) is { SenderId: "Local", RecipientId: "A", RawText: "X" },
                    "begin captures immutable payload without creating history");
                Check(store.BeginManualSend(a, "changed", 0, 9) is AlreadySending && store.GetDraftState(a)!.RawText == "X", "double click does not overwrite draft");
                Check(store.CompleteAttempt(send.MessageId, Ack("A", "X")) is Confirmed { DraftCleared: true }, "valid ACK atomically stores history and clears matching draft");
                Check(store.GetDraftState(a) is { RawText: null, Epoch: 1, Revision: 5 }, "ACK increments epoch without deleting revision high-water");
                Check(!store.SaveDraft(a, "old4", 0, 4).Saved && !store.SaveDraft(a, "X", 0, 5).Saved && !store.SaveDraft(a, "X", 1, 5).Saved,
                    "old epochs and old revisions cannot resurrect sent text");
                Check(store.SaveDraft(a, "next", 1, 6).Saved && store.CompleteAttempt(send.MessageId, Ack("A", "X")) is AlreadyConfirmed &&
                    store.GetDraftState(a)!.RawText == "next" && Number(f.Db, "SELECT count(*) FROM Message") == 1, "duplicate ACK does not clear new draft or duplicate history");
                Check(Text(f.Db, "SELECT senderId FROM Message") == "Local" && Text(f.Db, "SELECT remoteStoredAt FROM Message").StartsWith("2026-", StringComparison.Ordinal),
                    "sender snapshot retained and remote time does not choose persisted timestamp");
            }
            using var reopened = ChatStore.Open(f.Options);
            Check(reopened.GetDraftState(a) is { RawText: "next", Epoch: 1, Revision: 6 } && !reopened.SaveDraft(a, "X", 0, 500).Saved,
                "epoch and draft survive process-style reopen");
            Check(reopened.SaveDraft(a, "", 1, 7).Saved && reopened.GetDraftState(a) is { RawText: null, Revision: 7 } && !reopened.SaveDraft(a, "old", 1, 6).Saved,
                "emptying draft still advances high-water");
            Check(reopened.SaveDraft(a, "  \n", 1, 8).Saved, "draft may retain unfinished whitespace even though send rejects it");
            Reject(() => reopened.SaveDraft(a, "x", 1, 1_000_009), "revision jump bounded without overflow");
            Reject(() => reopened.SaveDraft(a, "\ud800", 1, 9), "draft never persists replacement characters for invalid Unicode");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, b) = Prepare(store);
            var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 5);
            store.SaveDraft(a, "Y", 0, 6);
            Check(store.CompleteAttempt(send.MessageId, Ack("A", "X")) is Confirmed { DraftCleared: false } && store.GetDraftState(a)!.RawText == "Y",
                "editing while sending preserves the new draft");
            Check(store.BeginManualSend(a, "wrong", 0, 6) is StaleDraft && Number(f.Db, "SELECT count(*) FROM ManualSendAttempt") == 1, "same-revision different text is stale and has no side effects");
            store.SaveDraft(b, "independent", 0, 1);
            Check(store.GetDraftState(a)!.RawText == "Y" && store.GetDraftState(b)!.RawText == "independent", "drafts isolated per conversation");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, b) = Prepare(store);
            var first = (AttemptStarted)store.BeginManualSend(a, "X", 0, 1);
            store.CompleteAttempt(first.MessageId, new UnconfirmedOutcome(), first.TryCount);
            Check(store.GetPreparedSend(first.MessageId) is null && Number(f.Db, "SELECT count(*) FROM Message") == 0, "unconfirmed has no history and cannot be prepared without a human retry");
            var retry = (AttemptStarted)store.BeginManualRetry(first.MessageId, "X", a, 0, 1);
            Check(retry.ReusedId && retry.MessageId == first.MessageId && retry.TryCount == 2 && retry.PreviousAttemptMayHaveDelivered, "manual retry retains id and increments attempt count");
            Check(store.CompleteAttempt(first.MessageId, new FailedOutcome("late"), 1) is Discarded { Reason: "stale-attempt" } &&
                !store.CancelAttempt(first.MessageId, 1).Cancelled && Text(f.Db, "SELECT attemptState FROM ManualSendAttempt") == "sending", "old failure and cancel cannot overwrite new try");
            Check(store.CompleteAttempt(first.MessageId, new RemoteStoredOutcome(DateTime.MinValue, new byte[32]), 2) is AckMismatch &&
                Text(f.Db, "SELECT attemptState FROM ManualSendAttempt") == "unconfirmed" && Number(f.Db, "SELECT count(*) FROM Message") == 0 && store.GetDraftState(a)!.RawText == "X",
                "wrong ACK fingerprint commits only unconfirmed state, retaining draft");
            var changed = (AttemptStarted)store.BeginManualRetry(first.MessageId, "Y", a, 0, 2);
            Check(!changed.ReusedId && changed.MessageId != first.MessageId && changed.PreviousAttemptMayHaveDelivered &&
                Text(f.Db, $"SELECT attemptState FROM ManualSendAttempt WHERE messageId='{first.MessageId}'") == "unconfirmed", "editing on retry creates a distinct immutable attempt");
            Check(store.BeginManualRetry(first.MessageId, "X", a, 0, 3) is AlreadySending, "another active send blocks retry before draft changes");
            store.CancelAttempt(changed.MessageId, changed.TryCount);
            Check(store.CompleteAttempt(changed.MessageId, new UnconfirmedOutcome(), changed.TryCount) is StateUpdated { NewState: "cancelled" }, "late timeout does not undo cancel");
            Check(store.CompleteAttempt(changed.MessageId, Ack("A", "Y")) is Confirmed, "valid late ACK after cancel records actual delivery");
            var moved = (AttemptStarted)store.BeginManualRetry(first.MessageId, "X", b, 0, 1);
            Check(moved.MessageId != first.MessageId && store.GetPreparedSend(moved.MessageId)!.RecipientId == "B", "changing target allocates a new id");
            var state = store.GetDraftState(a)!; store.SaveDraft(a, "fresh", state.Epoch, state.Revision + 1);
            Check(store.CompleteAttempt(first.MessageId, Ack("A", "X"), 1) is Confirmed { DraftCleared: false } && store.GetDraftState(a)!.RawText == "fresh",
                "earlier try's valid ACK records fact without clearing a newer generation draft");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, _) = Prepare(store);
            store.SaveDraft(a, "composer", 0, 3);
            var reaction = (AttemptStarted)store.BeginManualSend(a, "講太快", 0, 3, "reaction", "too-fast");
            Check(store.GetDraftState(a)!.RawText == "composer" && store.GetPreparedSend(reaction.MessageId) is { Kind: "reaction", Reaction: "too-fast" }, "quick reaction snapshot does not overwrite composer");
            Check(store.CompleteAttempt(reaction.MessageId, Ack("A", "講太快", "too-fast")) is Confirmed { DraftCleared: false } && store.GetDraftState(a) is { Epoch: 0, Revision: 3, RawText: "composer" },
                "quick reaction ACK cannot clear composer");
            var coincident = (AttemptStarted)store.BeginManualSend(a, "composer", 0, 3, "reaction", "too-fast");
            Check(store.CompleteAttempt(coincident.MessageId, Ack("A", "composer", "too-fast")) is Confirmed { DraftCleared: false } && store.GetDraftState(a)!.RawText == "composer",
                "quick reaction cannot clear draft even when its label is identical to composer text");
            var typed = (AttemptStarted)store.BeginManualSend(a, "composer", 0, 3, "text", "future-reaction");
            Check(store.CompleteAttempt(typed.MessageId, Ack("A", "composer", "future-reaction")) is Confirmed { DraftCleared: true }, "typed reaction still binds and clears its own draft");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, _) = Prepare(store);
            var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 1);
            store.ReplaceLocalIdentity("NewLocal", null, null);
            Check(store.CompleteAttempt(send.MessageId, Ack("A", "X")) is Discarded { Reason: "identity-replaced" } &&
                store.BeginManualRetry(send.MessageId, "X", a, 0, 1) is AttemptSuperseded && Number(f.Db, "SELECT count(*) FROM Message") == 0,
                "old ACK/retry cannot create new-identity history");
            Check(store.GetDraftState(a)!.RawText == "X" && store.GetPreparedSend(send.MessageId) is null, "replacement preserves draft without preparing superseded work");
            var next = (AttemptStarted)store.BeginManualSend(a, "X", 0, 1);
            Execute(f.Db, "UPDATE LocalIdentity SET id='ChangedAgain'");
            Check(store.CompleteAttempt(next.MessageId, Ack("A", "X")) is Discarded { Reason: "identity-replaced" } &&
                Text(f.Db, $"SELECT attemptState FROM ManualSendAttempt WHERE messageId='{next.MessageId}'") == "superseded", "raw identity change caught by final sender guard");
        }
        using (var f = new Fixture())
        {
            var faults = new Faults(); using var store = ChatStore.Open(f.Options with { Faults = faults }); var (a, _) = Prepare(store);
            var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 1);
            faults.Before = op => { if (op == "complete-attempt") throw new IOException("synthetic rollback"); };
            bool failed = false; try { store.CompleteAttempt(send.MessageId, Ack("A", "X")); } catch (IOException) { failed = true; }
            faults.Before = null;
            Check(failed && Number(f.Db, "SELECT count(*) FROM Message") == 0 && store.GetDraftState(a) is { Epoch: 0, RawText: "X" } &&
                Text(f.Db, "SELECT attemptState FROM ManualSendAttempt") == "sending", "failed completion rolls back attempt/history/epoch/draft together");
            faults.Before = op => { if (op == "complete-attempt") throw new SqliteException("injected FULL after clear", 13); };
            StoreException? full = null; try { store.CompleteAttempt(send.MessageId, Ack("A", "X")); } catch (StoreException e) { full = e; }
            faults.Before = null;
            Check(full?.Kind == StoreErrorKind.DiskFull && Number(f.Db, "SELECT count(*) FROM Message") == 0 && store.GetDraftState(a) is { Epoch: 0, Revision: 1, RawText: "X" } &&
                Text(f.Db, "SELECT attemptState FROM ManualSendAttempt") == "sending", "FULL injected after clear rolls back draft deletion and epoch bump too");
            store.CancelAttempt(send.MessageId);
            Execute(f.Db, $"UPDATE Conversation SET draftEpoch={long.MaxValue} WHERE id='{a}'");
            var exhausted = (AttemptStarted)store.BeginManualSend(a, "X", long.MaxValue, 1);
            Reject(() => store.CompleteAttempt(exhausted.MessageId, Ack("A", "X")), "epoch increment cannot wrap");
            Check(Number(f.Db, "SELECT count(*) FROM Message") == 0 && store.GetDraftState(a)!.Epoch == long.MaxValue, "overflow rejection has no partial completion");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = ((PeerRegistered)store.RegisterPeer("A", PairingState.Unpaired)).Conversation.ConversationId;
            Check(store.BeginManualSend(a, "X", 0, 1) is InvalidSend { Reason: "no-identity" }, "no identity cannot start a send");
            store.EnsureLocalIdentity("Local");
            Check(store.BeginManualSend(a, "X", 0, 1) is InvalidSend { Reason: "peer-not-paired" } && store.GetDraftState(a) is { Revision: 0, RawText: null },
                "unpaired send rejection has no draft side effects");
            store.SetPairingState("A", PairingState.Paired);
            Check(store.BeginManualSend(a, "X", 1, 1) is StaleDraft && Number(f.Db, "SELECT count(*) FROM ManualSendAttempt") == 0,
                "stale epoch begin never creates an attempt");
        }

        foreach (var order in Permutations(new[] { 0, 1, 2, 3 }))
        {
            using var f = new Fixture(); using var store = ChatStore.Open(f.Options); var (a, _) = Prepare(store);
            var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 5);
            var gates = Enumerable.Range(0, 4).Select(_ => new ManualResetEventSlim(false)).ToArray();
            try
            {
                var work = order.Select((operation, index) => Task.Factory.StartNew(() => {
                    if (!gates[index].Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("draft order gate");
                    try {
                        if (operation == 0) store.CompleteAttempt(send.MessageId, Ack("A", "X"));
                        else store.SaveDraft(a, operation == 3 ? "Y" : "X", 0, operation == 3 ? 6 : operation == 1 ? 4 : 5);
                    } finally { if (index < 3) gates[index + 1].Set(); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                gates[0].Set(); await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(15));
                var state = store.GetDraftState(a)!;
                Check(state.RawText == (Array.IndexOf(order, 3) < Array.IndexOf(order, 0) ? "Y" : null) && Number(f.Db, "SELECT count(*) FROM Message") == 1,
                    "24 prescribed multithreaded ACK/rev4/rev5/rev6 orders never resurrect sent X");
                Check(store.SaveDraft(a, "next", state.Epoch, state.Revision + 1).Saved, "fresh edit accepted after each interleaving");
            }
            finally { foreach (var gate in gates) gate.Dispose(); }
        }
        for (int i = 0; i < 8; i++)
        {
            using var f = new Fixture(); using var store = ChatStore.Open(f.Options); var (a, _) = Prepare(store);
            var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 5);
            using var start = new ManualResetEventSlim(false);
            var work = Enumerable.Range(0, 4).Select(op => Task.Factory.StartNew(() => {
                if (!start.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("race start");
                if (op == 0) store.CompleteAttempt(send.MessageId, Ack("A", "X"));
                else store.SaveDraft(a, op == 3 ? "Y" : "X", 0, op == 3 ? 6 : op == 1 ? 4 : 5);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            start.Set(); await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(15));
            Check(store.GetDraftState(a)!.RawText is null or "Y" && Number(f.Db, "SELECT count(*) FROM Message") == 1, "unconstrained concurrent ACK/save race preserves the invariant");
        }

        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options); var (a, _) = Prepare(store);
            var send = (AttemptStarted)store.BeginManualSend(a, "X", 0, 1); store.CompleteAttempt(send.MessageId, Ack("A", "X"));
            Check(!store.ApplyRemoteReceipt("B", send.MessageId, RemoteReceiptKind.Read, "Local").Changed &&
                !store.ApplyRemoteReceipt("A", "unknown", RemoteReceiptKind.Read, "Local").Changed, "forged and unknown receipts cannot create or change messages");
            Check(store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Read, "Local").Changed &&
                !store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Stored, "Local").Changed &&
                !store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Read, "Local").Changed, "read receipt raises status once and late stored never downgrades it");
            store.ReplaceLocalIdentity("NewLocal", null, null);
            Execute(f.Db, $"INSERT INTO Message(conversationId,direction,senderId,recipientId,messageId,rawText,kind,fingerprintAlg,contentFingerprint,createdUtc,remoteStoredAt) VALUES('{a}','outgoing','NewLocal','A','{send.MessageId}','new','text','nekochat-v2-content',zeroblob(32),'t','t')");
            Check(store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Read).Reason == "ambiguous-message", "receipt without original sender fails closed on cross-identity collision");
            Check(store.ApplyRemoteReceipt("A", send.MessageId, RemoteReceiptKind.Read, "NewLocal").Changed &&
                Number(f.Db, "SELECT count(*) FROM Message WHERE remoteReadAt IS NOT NULL") == 2, "explicit original sender disambiguates same message id");
        }

        int children = 0;
        foreach (string stage in new[] { "before", "after" })
        {
            using var f = new Fixture(); string a, id;
            using (var setup = ChatStore.Open(f.Options)) { (a, _) = Prepare(setup); id = ((AttemptStarted)setup.BeginManualSend(a, "X", 0, 5)).MessageId; }
            using var child = StartChild(f.Data, id, stage); children++;
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "READY:" + stage, "child reaches product CompleteAttempt boundary");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            using var recovered = ChatStore.Open(f.Options);
            Check(Number(f.Db, "SELECT count(*) FROM Message") == (stage == "after" ? 1 : 0) && recovered.GetDraftState(a) is { Revision: 5 } state &&
                state.Epoch == (stage == "after" ? 1 : 0) && state.RawText == (stage == "after" ? null : "X"), "real kill atomically preserves or clears draft with history");
            if (stage == "before") recovered.CompleteAttempt(id, Ack("A", "X"));
            Check(!recovered.SaveDraft(a, "X", 0, 4).Saved && !recovered.SaveDraft(a, "X", 0, 500).Saved && Number(f.Db, "SELECT count(*) FROM Message") == 1,
                "old save cannot revive draft after killed completion and recovery");
        }
        Check(children == 2, "exactly two bounded completion children");
        Console.WriteLine($"Storage send: {checks} draft, retry, identity, ACK and receipt checks passed;24 ordered races,8 free races,2 killed children.");
        return checks;
    }

    private static RemoteStoredOutcome Ack(string recipient, string body, string? reaction = null) =>
        new(DateTime.MinValue, SHA256.HashData(Encoding.UTF8.GetBytes("nekochat-v2-content\n" + recipient + "\n" + (reaction ?? "") + "\n" + body)));
    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 0) { yield return []; yield break; }
        foreach (int first in values) foreach (var tail in Permutations(values.Where(v => v != first).ToArray())) yield return new[] { first }.Concat(tail).ToArray();
    }
    private static Process StartChild(string data, string id, string stage)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(StorageSendChecks).Assembly.Location);
        foreach (string arg in new[] { ChildMode, data, id, stage }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
    }
    private static (string A, string B) Prepare(ChatStore store)
    {
        store.EnsureLocalIdentity("Local");
        return (((PeerRegistered)store.RegisterPeer("A", PairingState.Paired)).Conversation.ConversationId,
            ((PeerRegistered)store.RegisterPeer("B", PairingState.Paired)).Conversation.ConversationId);
    }
    private static SqliteConnection Connect(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON"; cmd.ExecuteNonQuery(); return c;
    }
    private static long Number(string path, string sql) { using var c = Connect(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
    private static string Text(string path, string sql) { using var c = Connect(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
    private static void Execute(string path, string sql) { using var c = Connect(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-send-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        internal Fixture() => Directory.CreateDirectory(Root);
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
