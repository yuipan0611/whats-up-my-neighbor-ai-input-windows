using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>
/// CM-10/11/12 (PMCM R2/R4/R8): principal-keyed classroom storage on real SQLite. The "source address" argument is exactly what the transport takes
/// from the accepted socket; nothing inside a message ever chooses the conversation. No Peer, key or pairing state exists in this file.
/// </summary>
internal static class ClassroomStoreChecks
{
    private const string X = "10.0.0.1", Y = "10.0.0.2", Z = "10.0.0.9", W = "10.0.0.7";

    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception($"Classroom store #{checks}: {name}"); }

        // principal key (CM-10)
        Check(ClassroomSource.Canonical(IPAddress.Parse("::ffff:10.0.0.1")) == "10.0.0.1" && ClassroomSource.Canonical(IPAddress.Parse("10.0.0.1")) == "10.0.0.1" && ClassroomPrincipalKey.ForSource("10.0.0.1") is { Value: "ip:10.0.0.1", IsRoster: false, SourceIp: "10.0.0.1" },
            "IPv4-mapped IPv6 is normalised to the IPv4 principal by the adapter");
        Check(ClassroomSource.Canonical(IPAddress.IPv6Loopback) is null && ClassroomSource.Canonical(IPAddress.Parse("fe80::1")) is null && ClassroomSource.Canonical(IPAddress.Any) is null && ClassroomSource.Canonical(IPAddress.Parse("224.0.0.1")) is null &&
            ClassroomSource.Canonical(IPAddress.Broadcast) is null && ClassroomSource.Canonical(null) is null && ClassroomPrincipalKey.ForSource("::ffff:10.0.0.1") is null && ClassroomPrincipalKey.ForSource("010.0.0.1") is null &&
            ClassroomPrincipalKey.ForSource("10.0.0.256") is null && ClassroomPrincipalKey.ForSource("10.0.0") is null && ClassroomPrincipalKey.ForSource(" 10.0.0.1") is null && ClassroomPrincipalKey.ForSource("0.0.0.0") is null,
            "IPv6, 0.0.0.0, multicast, broadcast and non-canonical text are not sources");
        Check(ClassroomPrincipalKey.TryParse("roster:abc-1", out var rk) && rk.IsRoster && rk.RosterEntryId == "abc-1" && !ClassroomPrincipalKey.TryParse("ip:010.0.0.1", out _) && !ClassroomPrincipalKey.TryParse("ip:::1", out _) &&
            !ClassroomPrincipalKey.TryParse("peer:1", out _) && !ClassroomPrincipalKey.TryParse("roster:bad id", out _), "principal text round-trips and rejects non-canonical forms");

        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X.ToString()), b = Add(store, "乙", Y.ToString());
            var pa = ClassroomPrincipalKey.ForRosterEntry(a);

            // receive: owner decided by the socket address only
            var r1 = Rx(store, X, new("m1", "hello from x"));
            Check(r1 is ClassroomStored { Principal: var p1, UnreadCount: 1 } && p1 == pa, "message from a roster address belongs to that row");
            Check(Rx(store, ClassroomSource.Canonical(IPAddress.Parse("::ffff:10.0.0.1"))!, new("m2", "mapped")) is ClassroomStored { Principal: var pm } && pm == pa, "an IPv4-mapped source reaches the same row");
            var z1 = Rx(store, Z, new("m1", "from stranger"));
            Check(z1 is ClassroomStored { Principal.Value: "ip:10.0.0.9" } && store.GetRosterSnapshot().Count == 2, "the same message id from another source is a different message, in 名單外");
            Check(Rx(store, X, new("m1", "hello from x")) is ClassroomDuplicate { Principal: var pd } && pd == pa && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal='roster:" + a + "'") == 2, "dedup key is (principal, messageId)");
            Check(Rx(store, X, new("m1", "different body")) is ClassroomConflict, "same id with other content is a conflict, nothing stored");
            Check(Rx(store, "::1", new("m9", "x")) is ClassroomRejected { Reason: "invalid-source" } && Rx(store, X, new("bad id", "x")) is ClassroomRejected { Reason: "invalid-id" } &&
                Rx(store, X, new("m8", "  ")) is ClassroomRejected { Reason: "invalid-text" } && Rx(store, X, new("m8", "ok", "BAD!")) is ClassroomRejected { Reason: "invalid-reaction" }, "invalid source, id, text and reaction are refused");
            Check(Count(f.Db, "SELECT count(*) FROM Peer") == 0 && Count(f.Db, "SELECT count(*) FROM Message") == 0 && store.GetLocalIdentitySnapshot() is { PublicSpkiBase64: null, CredentialRef: null }, "no peer, key or verified-message row is created");

            Check(Rx(store, Z, new("claims-1", "I am roster:" + a + " at 10.0.0.1 ip:10.0.0.1", "wave")) is ClassroomStored { Principal.Value: "ip:10.0.0.9" } && Texts(store, ClassroomPrincipalKey.ForRosterEntry(a)).Count == 2,
                "text that names another conversation or address does not move the message: only the socket address decides");
            // known boundary (R2): after a row's address moves, a late resend from the OLD address is a different principal
            store.SaveManualRosterEntry(b, new("乙", null, false, W.ToString()), Token(store));
            Rx(store, Y, new("late-1", "sent before the move"));
            Rx(store, W, new("late-1", "sent before the move"));
            Check(Texts(store, ClassroomPrincipalKey.ForRosterEntry(b)).SequenceEqual(["sent before the move"]) && Texts(store, ClassroomPrincipalKey.ForSourceIp("10.0.0.2")).SequenceEqual(["sent before the move"]),
                "KNOWN BOUNDARY: a resend from the old address lands in 名單外・old and is not de-duplicated across principals");
        }

        // drafts: guard, CM-12 size
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X.ToString()); var pa = ClassroomPrincipalKey.ForRosterEntry(a);
            Check(store.GetClassroomDraftState(pa) is { RawText: null, Epoch: 0, Revision: 0 }, "a conversation with no history starts at epoch 0 revision 0");
            Check(store.SaveClassroomDraft(pa, "draft one", 0, 1) is { Saved: true } && store.SaveClassroomDraft(pa, "stale", 0, 1) is { Saved: false, Reason: "stale-revision" } &&
                store.SaveClassroomDraft(pa, "stale epoch", 5, 2) is { Saved: false, Reason: "stale-epoch" } && store.GetClassroomDraftState(pa).RawText == "draft one", "draft saves are accepted only for the current epoch with a higher revision");
            Check(store.SaveClassroomDraft(ClassroomPrincipalKey.ForRosterEntry("11111111-1111-4111-8111-111111111111"), "x", 0, 1) is { Saved: false, Reason: "not-found" }, "no draft for a roster row that does not exist");
            string max = new('a', ClassroomLimits.MaxDraftBytes);
            Check(store.SaveClassroomDraft(pa, max, 0, 2) is { Saved: true } && store.GetClassroomDraftState(pa).RawText!.Length == ClassroomLimits.MaxDraftBytes, "CM-12: exactly 1 MiB is kept untruncated");
            Check(store.SaveClassroomDraft(pa, max + "a", 0, 3) is { Saved: false, Reason: "draft-too-large" } && store.GetClassroomDraftState(pa).RawText!.Length == ClassroomLimits.MaxDraftBytes &&
                store.SaveClassroomDraft(pa, new string('語', 349_526), 0, 3) is { Saved: false, Reason: "draft-too-large" }, "CM-12: over 1 MiB (by UTF-8 bytes) is refused with a clear reason and the stored draft is untouched");
            Check(store.SaveClassroomDraft(pa, max + "a", 0, 3) is { Saved: false, Reason: "draft-too-large", CurrentEpoch: 0, CurrentRevision: 2 }, "draft-too-large reports the current guard, not zeros");
            string longText = new('b', 3000);
            Check(store.SaveClassroomDraft(pa, longText, 0, 3) is { Saved: true } && store.BeginClassroomSend(pa, longText, 0, 3) is InvalidSend { Reason: "invalid-payload" } && store.GetClassroomDraftState(pa).RawText == longText,
                "CM-12: a text over the send limit survives as a draft and is only refused when sending");
        }

        // send / ACK / retry / cancel
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X.ToString()), none = Add(store, "丁", (string?)null); var pa = ClassroomPrincipalKey.ForRosterEntry(a); var pn = ClassroomPrincipalKey.ForRosterEntry(none);
            Check(store.BeginClassroomSend(pn, "hi", 0, 1) is InvalidSend { Reason: "no-address" } && Count(f.Db, "SELECT count(*) FROM ClassroomAttempt") == 0, "CM-04: a row without an address cannot be sent to, and nothing is recorded");
            store.SaveClassroomDraft(pa, "hello x", 0, 1);
            var begin = store.BeginClassroomSend(pa, "hello x", 0, 1) as AttemptStarted;
            Check(begin is { TryCount: 1, ReusedId: false } && store.GetClassroomPreparedSend(begin.MessageId) is { TargetIp: "10.0.0.1", RawText: "hello x" } && store.BeginClassroomSend(pa, "hello x", 0, 1) is AlreadySending, "an attempt resolves its current target and only one may be sending");
            byte[] fp = Convert.FromHexString(store.GetClassroomPreparedSend(begin!.MessageId)!.FingerprintHex);
            Check(store.CompleteClassroomAttempt(begin.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, fp), Z) is Discarded { Reason: "ack-binding" } && store.CompleteClassroomAttempt(begin.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, fp), null) is Discarded { Reason: "ack-binding" },
                "a stored-ACK from another address (or none) is discarded");
            Check(store.CompleteClassroomAttempt(begin.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, new byte[32]), X) is AckMismatch && store.GetClassroomPreparedSend(begin.MessageId) is null, "an ACK for different content marks the attempt unconfirmed");
            var retry = store.BeginClassroomRetry(begin.MessageId, pa, "hello x", 0, 1) as AttemptStarted;
            Check(retry is { ReusedId: true, TryCount: 2, PreviousAttemptMayHaveDelivered: true } && retry.MessageId == begin.MessageId, "manual retry keeps the message id");
            var confirmed = store.CompleteClassroomAttempt(begin.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, fp), X);
            Check(confirmed is Confirmed { DraftCleared: true } && store.GetClassroomDraftState(pa) is { RawText: null, Epoch: 1 } && store.CompleteClassroomAttempt(begin.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, fp), X) is AlreadyConfirmed,
                "confirmed send stores the outgoing message, clears the unchanged draft and moves the epoch");
            store.SaveClassroomDraft(pa, "second", 1, 2);
            var second = (AttemptStarted)store.BeginClassroomSend(pa, "second", 1, 2);
            store.SaveClassroomDraft(pa, "edited meanwhile", 1, 3);
            store.CompleteClassroomAttempt(second.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, ClassroomFpOf(store, second.MessageId)), X);
            Check(store.GetClassroomDraftState(pa).RawText == "edited meanwhile", "a late ACK never clears a draft that changed after the send began");
            var third = (AttemptStarted)store.BeginClassroomSend(pa, "edited meanwhile", 1, 3);
            Check(store.CancelClassroomAttempt(third.MessageId).Cancelled && store.CancelClassroomAttempt(third.MessageId).Reason == "already-cancelled", "cancel is idempotent");
            var fourth = (AttemptStarted)((store.BeginClassroomRetry(third.MessageId, pa, "edited meanwhile", 1, 3)));
            Check(fourth.MessageId == third.MessageId && fourth.PreviousAttemptMayHaveDelivered, "a cancelled attempt can be retried by hand with the same id");
            store.Dispose();
            using var reopened = ChatStore.Open(f.Options);
            Check(Text(f.Db, "SELECT attemptState FROM ClassroomAttempt WHERE messageId='" + fourth.MessageId + "'") == "unconfirmed" && reopened.GetClassroomPreparedSend(fourth.MessageId) is null, "reopening turns a half-sent attempt into unconfirmed; nothing is sent automatically");
            // address cleared: preparing is impossible, history stays
            reopened.SaveManualRosterEntry(a, new("甲", null, false, null), Token(reopened));
            Check(reopened.BeginClassroomRetry(fourth.MessageId, pa, "edited meanwhile", 1, 3) is InvalidSend { Reason: "no-address" } && reopened.ListClassroomMessages(pa).Items.Count == 2, "after the address is cleared a retry is refused and history stays");
        }

        // receipts (D-10) and remote read
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X.ToString()); var pa = ClassroomPrincipalKey.ForRosterEntry(a);
            var seq = ((ClassroomStored)Rx(store, X, new("in-1", "please read"))).LocalSeq;
            var open = new VisibleReadContext(pa.Value, true, true, false, true);
            Check(store.MarkClassroomVisibleRead(pa, [seq], new(null, true, true, false, true)) is { Marked: 0, Reason: "visibility-guard" }, "reading needs the real visible conversation");
            var read = store.MarkClassroomVisibleRead(pa, [seq], open);
            Check(read is { Marked: 1, Queued: 0 } && store.ListPendingClassroomReceipts().Count == 0, "D-10: outbound read receipts are off by default, nothing is queued");
            var seq2 = ((ClassroomStored)Rx(store, X, new("in-2", "second"))).LocalSeq;
            store.SetReadReceiptPolicy(true);
            Check(store.MarkClassroomVisibleRead(pa, [seq2], open) is { Marked: 1, Queued: 1 } && store.ListPendingClassroomReceipts().Single() is { MessageId: "in-2", TargetIp: "10.0.0.1" } receipt && store.CompleteClassroomReadReceipt(receipt) && store.ListPendingClassroomReceipts().Count == 0,
                "enabled: queued with the current address, removed after delivery");
            var seq3 = ((ClassroomStored)Rx(store, X, new("in-3", "third"))).LocalSeq;
            store.MarkClassroomVisibleRead(pa, [seq3], open); store.SetReadReceiptPolicy(false);
            Check(Count(f.Db, "SELECT count(*) FROM ClassroomReceiptOutbox") == 0, "turning receipts off drops the queue");
            // remote read receipt for a message we sent
            store.SaveClassroomDraft(pa, "out", 0, 1); var started = (AttemptStarted)store.BeginClassroomSend(pa, "out", 0, 1);
            Check(store.ApplyClassroomReceipt(X, started.MessageId, RemoteReceiptKind.Read) is { Changed: false, Reason: "pending-local-confirmation" }, "a receipt before local confirmation waits");
            store.CompleteClassroomAttempt(started.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, ClassroomFpOf(store, started.MessageId)), X);
            Check(store.ApplyClassroomReceipt(Z, started.MessageId, RemoteReceiptKind.Read) is { Changed: false, Reason: "not-found" } && Text(f.Db, "SELECT remoteReadAt IS NULL FROM ClassroomMessage WHERE messageId='" + started.MessageId + "'") == "1",
                "a read receipt from another address does not mark our message read");
            Check(store.ApplyClassroomReceipt(X, started.MessageId, RemoteReceiptKind.Read) is { Changed: true } && store.ApplyClassroomReceipt(X, started.MessageId, RemoteReceiptKind.Read) is { Changed: false, Reason: "unchanged" }, "the owner's read receipt applies once");
        }

        // clear (D-12) and tombstones
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X.ToString()); var pa = ClassroomPrincipalKey.ForRosterEntry(a);
            Rx(store, X, new("c-1", "old")); store.SaveClassroomDraft(pa, "unsent", 0, 1); store.SetReadReceiptPolicy(true);
            store.MarkClassroomVisibleRead(pa, [1], new(pa.Value, true, true, false, true)); var attempt = (AttemptStarted)store.BeginClassroomSend(pa, "unsent", 0, 1);
            var cleared = store.ClearClassroomConversation(pa);
            Check(cleared is { Messages: 1, Drafts: 1, Attempts: 1, Receipts: 1, NewEpoch: 1 } && Count(f.Db, "SELECT count(*) FROM ClassroomMessage")+Count(f.Db, "SELECT count(*) FROM ClassroomDraft")+Count(f.Db, "SELECT count(*) FROM ClassroomAttempt")+Count(f.Db, "SELECT count(*) FROM ClassroomReceiptOutbox") == 0,
                "D-12: messages, draft, attempt and unsent receipt are cleared together");
            Check(store.GetRosterSnapshot().Count == 1 && store.SaveClassroomDraft(pa, "late", 0, 2) is { Saved: false, Reason: "stale-epoch" } && store.SaveClassroomDraft(pa, "fresh", 1, 2) is { Saved: true }, "roster stays, and a save from before the clear is rejected");
            Check(Rx(store, X, new("c-1", "old")) is ClassroomClearedDuplicate && Count(f.Db, "SELECT count(*) FROM ClassroomMessage") == 0, "a late resend of a cleared message recreates nothing");
            Check(store.CompleteClassroomAttempt(attempt.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, new byte[32]), X) is Discarded { Reason: "not-found" }, "a late ACK for a cleared attempt does nothing");
        }

        // merge (CM-10): ip:x -> roster:id in one transaction
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            var pz = ClassroomPrincipalKey.ForSourceIp("10.0.0.9");
            string row = Add(store, "丙", (string?)null); var pr = ClassroomPrincipalKey.ForRosterEntry(row);
            Rx(store, Z, new("dup", "same")); Rx(store, Z, new("only-z", "z only"));
            store.SaveClassroomDraft(pz, "z draft", 0, 1);
            var sending = (AttemptStarted)store.BeginClassroomSend(pz, "z draft", 0, 1);
            // The roster row already has the same message id (it was received after the row got an address earlier), plus its own draft.
            store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.5"), Token(store));
            Rx(store, "10.0.0.5", new("dup", "same")); store.SaveClassroomDraft(pr, "z draft", 0, 1);
            store.SaveManualRosterEntry(row, new("丙", null, false, null), Token(store));
            var preview = store.PreviewManualRosterEntry(row, new("丙", null, false, "10.0.0.9"));
            Check(preview is { CanSave: true, UnknownMessagesToMerge: 2, MergeNotice: "此 IP 的 2 則名單外訊息會併入" } && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal='ip:10.0.0.9'") == 2, "preview counts the classroom messages of ip:x and moves nothing");
            Check(store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.9"), Token(store), new LegacyMergeConsent("10.0.0.9", 1)).Mutation.Reason == "legacy_merge_changed" && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal='ip:10.0.0.9'") == 2, "a wrong count refuses the whole save");
            var saved = store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.9"), Token(store), preview.ToMergeConsent());
            Check(saved.MergedMessages == 2 && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal='ip:10.0.0.9'") == 0 &&
                Texts(store, pr).OrderBy(t => t).SequenceEqual(["same", "z only"]) && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE messageId='dup'") == 1, "messages move to the row and a shared message id is kept once");
            Check(store.GetClassroomDraftState(pr).RawText == "z draft" && Count(f.Db, "SELECT count(*) FROM ClassroomDraft") == 1 && store.GetClassroomDraftState(pz).RawText is null && Text(f.Db, "SELECT principal FROM ClassroomAttempt WHERE messageId='" + sending.MessageId + "'") == pr.Value &&
                store.GetClassroomDraftState(pr).Epoch == 1 && store.GetClassroomDraftState(pz).Epoch == 1, "identical drafts are kept once, the attempt moves, and both draft epochs advance");
            Check(store.SaveClassroomDraft(pz, "stale", 0, 2) is { Saved: false, Reason: "stale-epoch" } && Rx(store, Z, new("after", "now mine")) is ClassroomStored { Principal: var after } && after == pr, "stale saves are rejected and new mail from the address goes to the row");
        }

        // CM-02c: a draft that exists on only one side moves; a conflict refuses the whole merge and changes nothing
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            var pz = ClassroomPrincipalKey.ForSourceIp("10.0.0.9");
            string row = Add(store, "丙", (string?)null); var pr = ClassroomPrincipalKey.ForRosterEntry(row);
            Rx(store, Z, new("only-z", "z only")); store.SaveClassroomDraft(pz, "only the stranger has a draft", 0, 1);
            var preview = store.PreviewManualRosterEntry(row, new("丙", null, false, "10.0.0.9"));
            store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.9"), Token(store), preview.ToMergeConsent());
            Check(store.GetClassroomDraftState(pr).RawText == "only the stranger has a draft" && Count(f.Db, "SELECT count(*) FROM ClassroomDraft") == 1, "CM-02c: when only the unknown source has a draft, the draft moves to the row");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            var pz = ClassroomPrincipalKey.ForSourceIp("10.0.0.9");
            string row = Add(store, "丙", "10.0.0.5"); var pr = ClassroomPrincipalKey.ForRosterEntry(row);
            Rx(store, "10.0.0.5", new("r1", "roster side")); store.SaveClassroomDraft(pr, "draft A", 0, 1);
            Rx(store, Z, new("z1", "stranger side")); store.SaveClassroomDraft(pz, "draft B", 0, 1);
            store.SaveManualRosterEntry(row, new("丙", null, false, null), Token(store));
            string Snapshot() => string.Join("|", Text(f.Db, "SELECT group_concat(principal||messageId||rawText,';') FROM (SELECT * FROM ClassroomMessage ORDER BY localSeq)"), Text(f.Db, "SELECT group_concat(principal||rawText,';') FROM (SELECT * FROM ClassroomDraft ORDER BY principal)"),
                Text(f.Db, "SELECT group_concat(principal||draftEpoch||'.'||draftRevision,';') FROM (SELECT * FROM ClassroomConversation ORDER BY principal)"), Text(f.Db, "SELECT group_concat(endpoint,';') FROM RosterEntry"));
            string before = Snapshot();
            var preview = store.PreviewManualRosterEntry(row, new("丙", null, false, "10.0.0.9"));
            Check(preview is { CanSave: false, Reason: "merge_conflict", MergeConflictReason: "draft-conflict" } && preview.MergeNotice!.Contains("無法併入") && preview.MergeNotice.Contains("送出或刪除") && preview.ToMergeConsent() is null, "CM-02c: two different drafts: the preview states the reason and the fix, and offers no consent");
            var refused = store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.9"), Token(store), new LegacyMergeConsent("10.0.0.9", 1));
            Check(refused.Mutation.Reason == "merge_conflict" && refused.MergedMessages == 0 && Snapshot() == before, "CM-02c: the save is refused as a whole; roster address, messages, drafts and epochs are unchanged");
            // fixing it (the user deletes the stranger's draft, per the stated solution) lets the merge through
            store.SaveClassroomDraft(pz, "", 0, 2);
            var fixedPreview = store.PreviewManualRosterEntry(row, new("丙", null, false, "10.0.0.9"));
            Check(fixedPreview.CanSave && store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.9"), Token(store), fixedPreview.ToMergeConsent()).MergedMessages == 1 && store.GetClassroomDraftState(pr).RawText == "draft A", "after the user clears the other draft the merge succeeds and keeps the non-empty one");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string row = Add(store, "丙", "10.0.0.5"); var pr = ClassroomPrincipalKey.ForRosterEntry(row);
            Rx(store, "10.0.0.5", new("same-id", "roster text")); Rx(store, Z, new("same-id", "different text")); Rx(store, Z, new("z-only", "z only"));
            store.SaveManualRosterEntry(row, new("丙", null, false, null), Token(store));
            string before = Text(f.Db, "SELECT group_concat(principal||messageId||rawText,';') FROM (SELECT * FROM ClassroomMessage ORDER BY localSeq)");
            var preview = store.PreviewManualRosterEntry(row, new("丙", null, false, "10.0.0.9"));
            Check(preview is { CanSave: false, MergeConflictReason: "message-conflict" } && preview.MergeNotice!.Contains("清除") , "CM-02c: one message id with different content on the two sides is a conflict, with its fix stated");
            Check(store.SaveManualRosterEntry(row, new("丙", null, false, "10.0.0.9"), Token(store), new LegacyMergeConsent("10.0.0.9", 2)).Mutation.Reason == "merge_conflict" &&
                Text(f.Db, "SELECT group_concat(principal||messageId||rawText,';') FROM (SELECT * FROM ClassroomMessage ORDER BY localSeq)") == before && Count(f.Db, "SELECT count(*) FROM ClassroomMessage") == 3, "CM-02c: refused whole; both copies and the unrelated message stay where they were");
            store.ClearClassroomConversation(ClassroomPrincipalKey.ForSourceIp("10.0.0.9"));
            var after = store.PreviewManualRosterEntry(row, new("丙", null, false, "10.0.0.9"));
            Check(after.CanSave && after.UnknownMessagesToMerge == 0, "after clearing the 名單外 conversation (the stated fix) there is nothing left to conflict");
        }
        // CSV: any conflicting line blocks the whole import (CM-03b + CM-02c)
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string row = Add(store, "丙", "10.0.0.5"); string other = Add(store, "丁", (string?)null);
            Rx(store, "10.0.0.5", new("same-id", "roster text")); Rx(store, Z, new("same-id", "different text")); Rx(store, W, new("w1", "w only"));
            store.SaveManualRosterEntry(row, new("丙", null, false, null), Token(store));
            var csv = RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip\n,丙,10.0.0.9\n,丁,10.0.0.7")));
            var notices = store.GetRosterImportMergeNotices(csv);
            Check(notices.Count == 2 && notices.Single(n => n.Endpoint == "10.0.0.9").ConflictReason == "message-conflict" && notices.Single(n => n.Endpoint == "10.0.0.9").Text.Contains("無法併入") && notices.Single(n => n.Endpoint == "10.0.0.7").ConflictReason is null, "CM-02b/c: the import preview marks only the conflicting line");
            string rosterBefore = Text(f.Db, "SELECT group_concat(id||coalesce(endpoint,''),';') FROM (SELECT * FROM RosterEntry ORDER BY id)");
            var blocked = store.ApplyRosterPreview(csv, notices);
            Check(blocked is { Applied: false, Reason: "merge_conflict" } && Text(f.Db, "SELECT group_concat(id||coalesce(endpoint,''),';') FROM (SELECT * FROM RosterEntry ORDER BY id)") == rosterBefore && !Directory.Exists(Path.Combine(f.Data, "backup")) &&
                Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal GLOB 'ip:*'") == 2, "CM-03b: one conflicting line blocks the whole import before any backup; the valid line is not applied either");
            Check(store.ApplyRosterPreview(csv, notices.Where(n => n.ConflictReason is null).ToArray()) is { Applied: false, Reason: "merge_conflict" }, "leaving the conflicting notice out of the confirmation does not get around the block");
        }

        // lifecycle guards: a cancelled receive or a view that went away leaves nothing behind (checked again right before COMMIT)
        using (var f = new Fixture())
        {
            bool live = true; var faults = new GuardFaults { Before = op => { if (op == "classroom-receive") live = false; } };
            using var store = ChatStore.Open(f.Options with { Faults = faults });
            Check(store.ReceiveClassroomMessage(Z, new("g1", "cancelled while waiting"), () => live) is ClassroomRejected { Reason: "receiver-not-ready" } && Count(f.Db, "SELECT count(*) FROM ClassroomMessage") == 0 && Count(f.Db, "SELECT count(*) FROM ClassroomConversation") == 0, "receive: cancelled before COMMIT -> rolled back, nothing stored, no ACK-able result");
            faults.Before = null; live = true;
            Check(store.ReceiveClassroomMessage(Z, new("g1", "still live"), () => live) is ClassroomStored && Count(f.Db, "SELECT count(*) FROM ClassroomMessage") == 1, "receive: a live request is stored");
        }
        using (var f = new Fixture())
        {
            bool visible = true; var faults = new GuardFaults();
            using var store = ChatStore.Open(f.Options with { Faults = faults });
            string row = Add(store, "甲", X); var pa = ClassroomPrincipalKey.ForRosterEntry(row);
            long seq = ((ClassroomStored)Rx(store, X, new("v1", "to read"))).LocalSeq; store.SetReadReceiptPolicy(true);
            faults.Before = op => { if (op == "classroom-visible-read") visible = false; };
            var stale = store.MarkClassroomVisibleRead(pa, [seq], new(pa.Value, true, true, false, true), () => visible);
            Check(stale is { Marked: 0, Queued: 0, Reason: "stale-view" } && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE localReadAt IS NOT NULL") == 0 && Count(f.Db, "SELECT count(*) FROM ClassroomReceiptOutbox") == 0, "read: view lost before COMMIT -> no read mark and no queued receipt");
            faults.Before = null; visible = true;
            Check(store.MarkClassroomVisibleRead(pa, [seq], new(pa.Value, true, true, false, true), () => visible) is { Marked: 1, Queued: 1 }, "read: a still-visible view marks and queues");
        }

        // merge notices from CSV (CM-02b) incl. legacy v1 rows
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X.ToString());
            Rx(store, Z, new("n-1", "z1")); Rx(store, Z, new("n-2", "z2"));
            var legacyCtx = store.GetLegacyReceiveContext("10.0.0.9"); store.ReceiveLegacyMessage(new("v1 text", null), legacyCtx, () => true);
            var csv = RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip\n,丙,10.0.0.9\n,丁,10.0.0.8")));
            var notices = store.GetRosterImportMergeNotices(csv);
            Check(notices.Count == 1 && notices[0] is { Line: 2, Messages: 3, Endpoint: "10.0.0.9", Text: "此 IP 的 3 則名單外訊息會併入丙" }, "CM-02b: the import preview names the row and the number of messages that would merge");
            var plain = store.ApplyRosterPreview(csv);
            Check(plain.Applied && plain.MergedMessages == 0 && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal='ip:10.0.0.9'") == 2, "applying without confirmation merges nothing");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            Add(store, "甲", X.ToString()); Rx(store, Z, new("n-1", "z1")); store.ReceiveLegacyMessage(new("v1 text", null), store.GetLegacyReceiveContext("10.0.0.9"), () => true);
            var csv = RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip\n,丙,10.0.0.9")));
            var notices = store.GetRosterImportMergeNotices(csv);
            Rx(store, Z, new("n-2", "arrives after the notice"));
            var stale = store.ApplyRosterPreview(csv, notices);
            Check(stale is { Applied: false, Reason: "legacy_merge_changed" } && store.GetRosterSnapshot().Count == 1 && !Directory.Exists(Path.Combine(f.Data, "backup")), "a count that changed after the notice refuses the import before any backup");
            var fresh = store.GetRosterImportMergeNotices(csv);
            var applied = store.ApplyRosterPreview(csv, fresh);
            string newRow = store.GetRosterSnapshot().Single(r => r.Entry.DisplayName == "丙").Entry.Id;
            Check(applied is { Applied: true, MergedMessages: 3 } && Texts(store, ClassroomPrincipalKey.ForRosterEntry(newRow)).Count == 2 &&
                store.ListLegacyMessages(new(newRow, null)).Items.Count == 1 && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal GLOB 'ip:*'") == 0, "confirmed import merges classroom and v1 messages into the new row in one transaction");
            var forged = store.ApplyRosterPreview(RosterImportPreview.Create(store.GetRosterSnapshot(), RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip\n,戊,10.0.0.6"))), [new(2, "戊", "10.0.0.7", 0)]);
            Check(forged is { Applied: false, Reason: "legacy_merge_mismatch" }, "a notice that does not match its import line is refused");
        }

        // CM-11: off-roster bounds refuse NEW messages and never delete stored rows
        using (var f = new Fixture())
        {
            var diag = new List<string>();
            using var store = ChatStore.Open(f.Options with { OffRoster = new(MaxSources: 2, MaxMessagesPerSource: 2, MaxMessagesTotal: 3, MaxTotalBytes: 40), DiagnosticSink = diag.Add });
            string a = Add(store, "甲", X.ToString());
            Rx(store, Z, new("1", "aaaa")); Rx(store, Z, new("2", "bbbb"));
            Check(Rx(store, Z, new("3", "cccc")) is ClassroomRejected { Reason: "offroster-limit" } && diag.Contains("classroom-offroster-limit scope=source"), "per-source bound refuses the new message and writes a content-free diagnostic");
            Rx(store, W, new("4", "dddd"));
            Check(Rx(store, "10.0.0.20", new("5", "e")) is ClassroomRejected && diag.Contains("classroom-offroster-limit scope=sources") && Count(f.Db, "SELECT count(*) FROM ClassroomMessage WHERE principal GLOB 'ip:*'") == 3,
                "source-count bound refuses a third unknown source");
            Check(Rx(store, W, new("6", "ffff")) is ClassroomRejected && diag.Contains("classroom-offroster-limit scope=total"), "total bound refuses the new message");
            Check(Rx(store, X, new("7", "roster is never limited")) is ClassroomStored && Texts(store, ClassroomPrincipalKey.ForSourceIp("10.0.0.9")).SequenceEqual(["aaaa", "bbbb"]) && diag.All(d => !d.Contains("10.0.0.") && !d.Contains("aaaa")), "roster senders are not bounded, stored rows are untouched, diagnostics carry no address or content");
        }
        using (var f = new Fixture())
        {
            var diag = new List<string>();
            using var store = ChatStore.Open(f.Options with { OffRoster = new(MaxSources: 10, MaxMessagesPerSource: 10, MaxMessagesTotal: 10, MaxTotalBytes: 10), DiagnosticSink = diag.Add });
            Rx(store, Z, new("1", "123456"));
            Check(Rx(store, Z, new("2", "12345")) is ClassroomRejected && diag.Contains("classroom-offroster-limit scope=bytes") && Texts(store, ClassroomPrincipalKey.ForSourceIp("10.0.0.9")).Count == 1, "byte bound counts UTF-8 bytes");
            bool bad = false; try { ChatStore.Open(f.Options with { OffRoster = new(MaxMessagesPerSource: 10, MaxMessagesTotal: 5) }).Dispose(); } catch (ArgumentOutOfRangeException) { bad = true; }
            Check(bad, "inconsistent bounds are refused at open");
        }

        // NCM1c (1): v1 legacy messages from off-roster sources share the CM-11 budget
        using (var f = new Fixture())
        {
            var diag = new List<string>();
            using var store = ChatStore.Open(f.Options with { OffRoster = new(MaxSources: 2, MaxMessagesPerSource: 2, MaxMessagesTotal: 3, MaxTotalBytes: 1000), DiagnosticSink = diag.Add });
            string a = Add(store, "甲", X);
            LegacyReceiveResult Legacy(string ip, string text) => store.ReceiveLegacyMessage(new(text, null), store.GetLegacyReceiveContext(ip), () => true);
            Check(Legacy(Z, "v1-1").Stored && Rx(store, Z, new("c1", "classroom-1")) is ClassroomStored, "off-roster v1 and classroom messages from one source both count");
            Check(Legacy(Z, "v1-2") is { Stored: false, Reason: "offroster-limit" } && Rx(store, Z, new("c2", "classroom-2")) is ClassroomRejected { Reason: "offroster-limit" } &&
                diag.Contains("classroom-offroster-limit scope=source") && Count(f.Db, "SELECT count(*) FROM LegacyMessage") == 1 && Count(f.Db, "SELECT count(*) FROM ClassroomMessage") == 1, "per-source bound is shared by both paths and stores nothing more");
            Check(Legacy(W, "v1-w").Stored && Legacy("10.0.0.20", "v1-new") is { Stored: false, Reason: "offroster-limit" } && diag.Contains("classroom-offroster-limit scope=sources"), "a third unknown source is refused for v1 too");
            Check(Legacy(X, "from roster 1").Stored && Legacy(X, "from roster 2").Stored && Legacy(X, "from roster 3").Stored && Legacy(X, "from roster 4").Stored && store.ListLegacyMessages(new(a, null)).Items.Count == 4, "roster members' v1 history has no bound");
            Check(Count(f.Db, "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL") == 2 && diag.All(d => !d.Contains("10.0.0.") && !d.Contains("v1")), "nothing is deleted and diagnostics hold no address or content");
        }
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options with { OffRoster = new(MaxMessagesPerSource: 10, MaxMessagesTotal: 10, MaxTotalBytes: 12) });
            Check(store.ReceiveLegacyMessage(new("12345678", null), store.GetLegacyReceiveContext(Z), () => true).Stored && store.ReceiveLegacyMessage(new("12345", null), store.GetLegacyReceiveContext(Z), () => true) is { Stored: false, Reason: "offroster-limit" },
                "v1 byte bound counts UTF-8 bytes");
        }

        // NCM1c (2): CM-17 removing a roster row keeps the conversation
        using (var f = new Fixture())
        {
            using var store = ChatStore.Open(f.Options);
            string a = Add(store, "甲", X); var pa = ClassroomPrincipalKey.ForRosterEntry(a);
            store.SaveManualRosterEntry(a, new("甲改", null, false, X), Token(store));
            Rx(store, X, new("r-1", "before delete")); store.SaveClassroomDraft(pa, "keep me", 0, 1);
            store.ReceiveLegacyMessage(new("v1 hello", null), store.GetLegacyReceiveContext(X), () => true);
            var preview = store.PreviewRosterDelete(a);
            Check(preview is { CanDelete: true, DisplayName: "甲改", ClassroomMessages: 1, LegacyMessages: 1 } && preview.Notice.Contains("對話會保留") && preview.Notice.Contains("甲改（已從名單移除）") && store.GetRosterSnapshot().Count == 1, "delete preview says the conversation is kept and writes nothing");
            Check(store.DeleteRosterEntry(a, Token(store), new RosterDeleteConfirmation("someone-else", "甲改")).Reason == "confirmation_mismatch" && store.DeleteRosterEntry(a, Token(store), new RosterDeleteConfirmation(a, "舊名")).Reason == "confirmation_mismatch" && store.GetRosterSnapshot().Count == 1, "a confirmation for another row or an outdated name deletes nothing");
            Check(store.DeleteRosterEntry(a, Token(store), preview.ToConfirmation()).Reason == "deleted" && store.GetRosterSnapshot().Count == 0, "confirmed delete removes the row");
            var summary = store.ListClassroomConversations().Single();
            Check(summary is { Removed: true, DisplayName: "甲改", Label: "甲改（已從名單移除）", CanSend: false, Messages: 1 } && Texts(store, pa).SequenceEqual(["before delete"]), "the conversation stays, labelled with the name at deletion");
            Check(store.BeginClassroomSend(pa, "hi", 0, 1) is InvalidSend { Reason: "removed" } && store.SaveClassroomDraft(pa, "x", 0, 2) is { Saved: false, Reason: "not-found" } && store.GetClassroomDraftState(pa).RawText == "keep me", "a removed row cannot be sent to; its stored draft is untouched");
            Check(store.MarkClassroomVisibleRead(pa, [1], new(pa.Value, true, true, false, true)).Marked == 1, "history of a removed row can still be read");
            Check(Rx(store, X, new("r-2", "after delete")) is ClassroomStored { Principal.Value: "ip:10.0.0.1" } && store.ListClassroomConversations().Count == 2, "mail from the old address now lands in 名單外");
            Check(Count(f.Db, "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NOT NULL") == 0 && Count(f.Db, "SELECT count(*) FROM LegacyMessage") == 1, "KNOWN LIMIT: v1 rows lose their roster owner (FK) but are kept, now under 名單外・IP");
            string again = Add(store, "甲", X);
            Check(again != a && Texts(store, ClassroomPrincipalKey.ForRosterEntry(again)).Count == 0 && Texts(store, pa).Count == 1, "adding the person again makes a new row; the old conversation is not re-attached by itself");
            Check(store.ClearClassroomConversation(pa).Messages == 1 && store.ListClassroomConversations().All(c => c.Principal != pa), "a removed row's conversation can be cleared by the user");
        }

        // migration: old history is preserved, frozen schema untouched, new schema guarded
        using (var f = new Fixture())
        {
                        using (var old = ChatStore.Open(f.Options with { Migrations = Migration.Default.Take(6).ToArray() }))
            {
                old.EnsureLocalIdentity("Local"); old.RegisterPeer("A", PairingState.Paired);
                old.ReceiveVerifiedMessage(new("A", "keep-1", "Local", "old verified history"));
                old.ReceiveLegacyMessage(new("old v1", null), old.GetLegacyReceiveContext("10.0.0.9"), () => true);
            }
            using var current = ChatStore.Open(f.Options);
            Check(Count(f.Db, "PRAGMA user_version") == 8 && Count(f.Db, "SELECT count(*) FROM Message") == 1 && Count(f.Db, "SELECT count(*) FROM LegacyMessage") == 1 && Count(f.Db, "SELECT count(*) FROM ClassroomMessage") == 0 &&
                Text(f.Db, "SELECT pairingState FROM Peer WHERE id='A'") == "paired", "additive migration keeps old key-based history exactly where it was; nothing is reassigned");
            string row = Add(current, "甲", X.ToString());
            Rx(current, Z, new("k", "x"));
            current.SaveManualRosterEntry(row, new("甲", null, false, "10.0.0.9"), Token(current), new LegacyMergeConsent("10.0.0.9", 2));
            Check(Count(f.Db, "SELECT count(*) FROM Message WHERE senderId='A'") == 1 && Count(f.Db, "SELECT count(*) FROM Peer") == 1, "merging never touches key-based Message or Peer rows");
        }
        using (var f = new Fixture())
        {
            using (var s = ChatStore.Open(f.Options)) { }
            Execute(f.Db, "DROP TRIGGER ClassroomMessage_immutable");
            StoreException? error = null; try { using var tampered = ChatStore.Open(f.Options); } catch (StoreException e) { error = e; }
            Check(error?.Kind == StoreErrorKind.UnrecognizedDatabase, "a changed classroom schema is refused at open");
        }

        Console.WriteLine($"Classroom store: {checks} principal key, receive/dedup, draft guard, send/ACK, receipt, clear, merge, off-roster bound and migration checks passed on real SQLite.");
        return checks;
    }

    private static ClassroomReceiveResult Rx(ChatStore s, string ip, ClassroomIncoming m) => s.ReceiveClassroomMessage(ip, m, () => true);
    private static byte[] ClassroomFpOf(ChatStore store, string messageId) => Convert.FromHexString(store.GetClassroomPreparedSend(messageId)!.FingerprintHex);
    private static string Add(ChatStore store, string name, string? ip)
    {
        var result = store.SaveManualRosterEntry(null, new(name, null, false, ip), Token(store));
        if (!result.Changed) throw new Exception("Classroom store fixture: " + result.Reason);
        return result.EntryId!;
    }
    private static string Token(ChatStore store) => RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot());
    private static IReadOnlyList<string> Texts(ChatStore store, ClassroomPrincipalKey p) => store.ListClassroomMessages(p).Items.Select(m => m.RawText).ToArray();
    private static long Count(string path, string sql) { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private static string Text(string path, string sql) { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? ""; }
    private static void Execute(string path, string sql) { using var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private sealed class GuardFaults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-classroom-store-" + Guid.NewGuid().ToString("N"));
        internal string Db => Path.Combine(Data, "nekochat.db");
        internal ChatStoreOptions Options => new(Data) { UtcNow = () => new DateTime(2026, 11, 3, 3, 4, 5, DateTimeKind.Utc) };
        internal Fixture() => Directory.CreateDirectory(Data);
        public void Dispose() => Directory.Delete(Data, true);
    }
}
