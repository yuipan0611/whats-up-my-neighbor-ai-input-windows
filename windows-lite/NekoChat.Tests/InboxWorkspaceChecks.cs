using NekoChat.Core;
using NekoChat.Core.Presentation;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class InboxWorkspaceChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Inbox workspace: " + name); }
        using (var f = new Fixture())
        {
            Check(f.Store.GetInboxSnapshot() is { Entries.Count: 1, TotalUnread: 0 }, "unlisted known peer visible without creating a label");
            string student = f.Add("Student", "10.0.0.1", "0002", true), teacher = f.Add("Teacher", "10.0.0.2", null, false, true), alias = f.Add("Alias", "10.0.0.3", "0010", true);
            string incoming = Guid.NewGuid().ToString("D"); f.Receive(incoming, "confirmed history");
            f.Legacy("10.0.0.1", "unverified row history"); f.Legacy("10.0.0.4", "unmatched history");
            var snapshot = f.Store.GetInboxSnapshot();
            Check(snapshot.Entries.Take(3).Select(e => e.RosterId).SequenceEqual(new[] { teacher, student, alias }), "teacher then numeric student number, stable rows (not recency/name) order");
            Check(snapshot.TotalUnread == 3 && snapshot.Entries.Single(e => e.RosterId == student).TotalUnread == 2 && snapshot.Entries.Single(e => e.RosterId == alias).Unread == 1,
                "total unread deduplicates aliased conversation while keeping per-scope legacy unread");
            Check(snapshot.Entries.Single(e => e.RosterId == student) is { LastIsLegacy: true, LastText: "unverified row history" } && snapshot.Entries.Single(e => e.Id == "legacy-ip:10.0.0.4").NotificationName == "未驗證來源",
                "separate legacy summary and private notification label exclude unknown source IP");
            var draft = f.Store.GetDraftState(f.Conversation)!; f.Store.SaveDraft(f.Conversation, "manual only", draft.Epoch, draft.Revision + 1);
            var started = (AttemptStarted)f.Store.BeginManualSend(f.Conversation, "manual only", draft.Epoch, draft.Revision + 1); f.Store.CompleteAttempt(started.MessageId, new UnconfirmedOutcome());
            var entry = f.Store.GetInboxSnapshot().Entries.Single(e => e.RosterId == alias);
            Check(entry.Attempt is { State: "unconfirmed", RawSnapshot: "manual only" } && entry.Draft?.RawText == "manual only" && entry.LastText == "confirmed history",
                "unconfirmed attempt stays in composer projection, never history snippet");
            f.Store.CancelAttempt(started.MessageId);
            Check(f.Store.GetInboxSnapshot().Entries.Single(e => e.RosterId == alias).Attempt is { State: "cancelled" }, "cancelled attempt remains visible as cancelled in composer projection");
            f.Store.DeleteRosterEntry(student, RosterImportPreview.ComputeSnapshotToken(f.Store.GetRosterSnapshot()));
            string reused = f.Add("New IP owner", "10.0.0.1", "0003", false);
            snapshot = f.Store.GetInboxSnapshot();
            Check(snapshot.Entries.Single(e => e.RosterId == reused).LegacyUnread == 0 && snapshot.Entries.Single(e => e.Id == "legacy-ip:10.0.0.1").LegacyUnread == 1,
                "deleted-row legacy history remains findable and never transfers to reused IP owner");
            Check(f.Store.GetUnreadCount(f.Conversation) == 1 && f.Store.ListLegacyMessages(new(null, "10.0.0.1")).Items.Single().LocalReadAt is null,
                "all Inbox reads leave local unread and receipt state untouched");
            f.Store.ClearConversation(f.Conversation); Check(f.Store.GetInboxSnapshot().Entries.Single(e => e.RosterId == alias).Draft?.RawText is null,
                "clear reflected without stale draft/attempt/message snippet");
        }
        using (var f = new Fixture())
        {
            var buffer = new DraftEditBuffer(f.Store.GetDraftState(f.Conversation)!); var sent = buffer.Edit("sent text");
            Check(buffer.CompleteWrite(sent, f.Save(sent)) && !buffer.Dirty, "confirmed save updates matching editor generation");
            Check(sent.Persist(f.Store, f.Conversation) is { Saved: true, Reason: "already-current" }, "duplicate flush confirms exact durable snapshot instead of falsely failing exit");
            var attempt = (AttemptStarted)f.Store.BeginManualSend(f.Conversation, sent.Text, sent.Epoch, sent.Revision);
            var edited = buffer.Edit("new edit before ACK callback");
            var prepared = f.Store.GetPreparedSend(attempt.MessageId)!;
            var complete = f.Store.CompleteAttempt(attempt.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, Convert.FromHexString(prepared.FingerprintHex)));
            Check(complete is Confirmed { DraftCleared: true } && !f.Save(edited).Saved, "own ACK can precede a queued real user edit; old epoch correctly rejected by SQLite");
            var applied = buffer.ApplyOwnAckClear(sent, f.Store.GetDraftState(f.Conversation)!);
            Check(applied.Applied && applied.PreserveEdit is { Text: "new edit before ACK callback" } && buffer.Text == edited.Text && buffer.Dirty,
                "only actual post-send user edit gets a new-epoch write token after own ACK");
            Check(buffer.CompleteWrite(applied.PreserveEdit!, f.Save(applied.PreserveEdit!)) && f.Store.GetDraftState(f.Conversation)?.RawText == edited.Text,
                "post-send edit persisted without reviving sent draft");
            Check(!buffer.CompleteWrite(sent, new(true, "saved", sent.Epoch, sent.Revision)) && !f.Save(sent).Saved && !buffer.ApplyOwnAckClear(sent, f.Store.GetDraftState(f.Conversation)!).Applied,
                "late save or ACK callback cannot rebase or restore obsolete text");
        }
        using (var f = new Fixture())
        {
            var buffer = new DraftEditBuffer(f.Store.GetDraftState(f.Conversation)!); var sent = buffer.Edit("send then clear"); f.Save(sent);
            var started = (AttemptStarted)f.Store.BeginManualSend(f.Conversation, sent.Text, sent.Epoch, sent.Revision); var prepared = f.Store.GetPreparedSend(started.MessageId)!;
            var clear = buffer.BeginClear(); f.Store.ClearConversation(f.Conversation); var state = f.Store.GetDraftState(f.Conversation)!;
            Check(buffer.CompleteClear(clear, state) && buffer.Text == "" && !f.Save(sent).Saved, "explicit clear invalidates queued writes and updates editor to durable clear epoch");
            Check(!buffer.ApplyOwnAckClear(sent, state).Applied && f.Store.CompleteAttempt(started.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, Convert.FromHexString(prepared.FingerprintHex))) is Discarded,
                "old in-flight send callback cannot resurrect cleared history or draft");
            var newer = buffer.Edit("real edit after clear"); Check(f.Save(newer).Saved, "a new explicit edit after clear uses fresh epoch");
            var fail = buffer.BeginClear(); Check(!buffer.CompleteClear(fail, null) && buffer.Text == newer.Text && !buffer.Clearing, "failed clear restores editing without claiming local deletion");
            var foreign = new DraftEditBuffer(state).Edit("foreign"); Check(!buffer.IsCurrent(foreign), "tickets from another buffer cannot mutate this composer");
        }
        using (var f = new Fixture())
        {
            var buffer = new DraftEditBuffer(f.Store.GetDraftState(f.Conversation)!); var sent = buffer.Edit("no newer edit"); f.Save(sent);
            var started = (AttemptStarted)f.Store.BeginManualSend(f.Conversation, sent.Text, sent.Epoch, sent.Revision); var prepared = f.Store.GetPreparedSend(started.MessageId)!;
            f.Store.CompleteAttempt(started.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, Convert.FromHexString(prepared.FingerprintHex)));
            Check(buffer.ApplyOwnAckClear(sent, f.Store.GetDraftState(f.Conversation)!) is { Applied: true, PreserveEdit: null } && buffer.Text == "" && !buffer.Dirty, "own ACK clears composer when no new user edit exists");
        }
        using (var f = new Fixture())
        {
            await using var worker = new ChatStoreWorker(f.Store, 1); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var first = worker.Run(_ => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException(); return 1; });
            if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); var second = worker.Run(s => s.GetInboxSnapshot()); bool full = false;
            try { _ = worker.Run(_ => 3); } catch (InvalidOperationException) { full = true; }
            release.Set(); Check(full && await first == 1 && (await second).Entries.Count == 1, "one serial worker stays bounded and signals overflow without losing accepted work");
            using var cancellation = new CancellationTokenSource(); entered.Reset(); release.Reset(); var block = worker.Run(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(6)); return 1; }); entered.Wait(TimeSpan.FromSeconds(3));
            var cancelled = worker.Run(s => s.EnsureLocalIdentity("should-not-write"), cancellation.Token); cancellation.Cancel(); release.Set(); await block;
            bool cancelledResult = false; try { await cancelled; } catch (OperationCanceledException) { cancelledResult = true; }
            Check(cancelledResult && f.Store.GetLocalIdentitySnapshot().Id == "Local1", "cancelled queued command never runs its database mutation");
            await worker.DisposeAsync(); Check(f.Store.GetInboxSnapshot().Entries.Count == 1, "draining worker leaves borrowed store usable");
        }
        using (var f = new Fixture())
        {
            using var life = new CancellationTokenSource(); await using var worker = new ChatStoreWorker(f.Store, lifetime: life.Token); life.Cancel(); bool rejected = false;
            try { _ = worker.Run(s => s.GetInboxSnapshot()); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && (await worker.Run(s => s.GetInboxSnapshot(), CancellationToken.None)).Entries.Count == 1, "shutdown cancels ordinary queued work while explicit critical final flush can still run before drain");
        }
        Console.WriteLine($"Inbox workspace: {checks} real SQLite summaries, editor generations, own ACK/clear races and bounded worker checks."); return checks;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-inbox-workspace-" + Guid.NewGuid().ToString("N"));
        internal ChatStore Store { get; }
        internal string Conversation { get; }
        internal Fixture() { Directory.CreateDirectory(_root); Store = ChatStore.Open(new(_root)); Store.EnsureLocalIdentity("Local1"); Conversation = ((PeerRegistered)Store.RegisterPeer("PeerA", PairingState.Paired)).Conversation.ConversationId; }
        internal string Add(string name, string ip, string? student, bool bind, bool teacher = false)
        { string Token() => RosterImportPreview.ComputeSnapshotToken(Store.GetRosterSnapshot()); string id = Store.SaveManualRosterEntry(null, new(name, student, teacher, ip), Token()).EntryId!; if (bind) Store.BindPairedRosterPeer(id, "PeerA", Token()); return id; }
        internal void Receive(string id, string text)
        { if (Store.ReceiveVerifiedMessage(new("PeerA", id, "Local1", text)) is not Stored) throw new Exception("fixture receive failed"); }
        internal void Legacy(string ip, string text) { var context = Store.GetLegacyReceiveContext(ip); Store.ReceiveLegacyMessage(new(text, null), context, () => true); }
        internal DraftSaveResult Save(DraftWriteSnapshot snapshot) => Store.SaveDraft(Conversation, snapshot.Text, snapshot.Epoch, snapshot.Revision);
        public void Dispose() { Store.Dispose(); Directory.Delete(_root, true); }
    }
}
