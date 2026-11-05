using NekoChat.Core.Storage;
using NekoChat.Core.Roster;

namespace NekoChat.Tests;

internal static class MessageRecoveryChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string label) { count++; if (!value) throw new Exception(label); }
        string root = Path.Combine(Path.GetTempPath(), "neko-message-recovery-" + Guid.NewGuid().ToString("N"));
        var principal = ClassroomPrincipalKey.ForSourceIp("10.0.0.11");
        var other = ClassroomPrincipalKey.ForSourceIp("10.0.0.12");
        string firstId; long survivor;
        try
        {
            using (var store = ChatStore.Open(new(root)))
            {
                Check(store.SaveClassroomDraft(principal, "第一則失敗", 0, 1).Saved, "save first draft");
                var first = (AttemptStarted)store.BeginClassroomSend(principal, "第一則失敗", 0, 1); firstId = first.MessageId;
                store.CompleteClassroomAttempt(firstId, new FailedOutcome("offline"), null, first.TryCount);
                Check(store.SaveClassroomDraft(principal, "第二則", 0, 2).Saved, "new edit after failure");
                var second = (AttemptStarted)store.BeginClassroomSend(principal, "第二則", 0, 2);
                Check(firstId != second.MessageId && store.ListClassroomPendingMessages(principal).Count == 2, "failure never blocks or replaces next independent send");
                store.RecordClassroomRoute(second.MessageId, 1, ClassroomRoute.Classroom, DateTime.UtcNow);
                Check(store.CompleteClassroomAttempt(second.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, ChatStore.ClassroomFingerprint("第二則", null)), "10.0.0.11", 1) is Confirmed { DraftCleared: true }, "second send can be confirmed while first remains failed");
                survivor = store.ListClassroomMessages(principal).Items.Single().LocalSeq;
                Check(store.ListClassroomPendingMessages(principal).Single().Attempt is { State: "failed", RawSnapshot: "第一則失敗" }, "first remains available for manual retry");
                var draft = store.GetClassroomDraftState(principal);
                Check(store.SaveClassroomDraft(principal, "目前編輯不可丟", draft.Epoch, draft.Revision + 1).Saved, "new unsent draft");
                Check(!store.DeleteClassroomPendingMessage(other, firstId, 1).Deleted && !store.DeleteClassroomPendingMessage(principal, firstId, 2).Deleted, "wrong scope and stale try cannot delete");
                Check(store.DeleteClassroomPendingMessage(principal, firstId, 1).Deleted && store.GetClassroomDraftState(principal).RawText == "目前編輯不可丟", "deleting failure preserves newer draft");
                Check(store.CompleteClassroomAttempt(firstId, new RemoteStoredOutcome(DateTime.UtcNow, ChatStore.ClassroomFingerprint("第一則失敗", null)), "10.0.0.11", 1) is Discarded, "late ACK cannot revive deleted attempt");

                var rx = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.11", new("incoming-delete", "刪這一則"), () => true);
                Check(!store.DeleteClassroomMessage(other, rx.LocalSeq).Deleted, "wrong conversation cannot delete message");
                Check(store.DeleteClassroomMessage(principal, rx.LocalSeq).Deleted && !store.DeleteClassroomMessage(principal, rx.LocalSeq).Deleted, "single message deletion is idempotent");
                Check(store.ReceiveClassroomMessage("10.0.0.11", new("incoming-delete", "刪這一則"), () => true) is ClassroomClearedDuplicate, "remote retransmit cannot revive local deletion");
                Check(store.ListClassroomMessages(principal).Items.Single().LocalSeq == survivor && store.GetClassroomDraftState(principal).RawText == "目前編輯不可丟", "other message and draft remain");

                var current = store.GetClassroomDraftState(principal);
                var pending = (AttemptStarted)store.BeginClassroomSend(principal, current.RawText!, current.Epoch, current.Revision);
                Check(!store.DeleteClassroomPendingMessage(principal, pending.MessageId, 1).Deleted, "active send cannot be dismissed");
                store.CompleteClassroomAttempt(pending.MessageId, new UnconfirmedOutcome(), null, 1);
                Check(store.DeleteClassroomPendingMessage(principal, pending.MessageId, 1).Deleted && store.GetClassroomDraftState(principal) is { RawText: null } cleared && cleared.Epoch == current.Epoch + 1, "deleting exact idle attempt clears only its matching draft and advances epoch");
                Check(!store.SaveClassroomDraft(principal, current.RawText!, current.Epoch, current.Revision + 1).Saved, "late autosave cannot restore deleted draft");

                // Outgoing deletion must also discard its confirmed attempt, so a duplicate ACK cannot recreate it.
                var rxOther = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.12", new("other", "另一個對話"), () => true);
                Check(store.DeleteClassroomMessage(principal, survivor).Deleted && store.CompleteClassroomAttempt(second.MessageId, new RemoteStoredOutcome(DateTime.UtcNow, ChatStore.ClassroomFingerprint("第二則", null)), "10.0.0.11", 1) is Discarded, "outgoing deletion survives late ACK");
                Check(store.ListClassroomMessages(other).Items.Single().LocalSeq == rxOther.LocalSeq, "other conversation unchanged");

                var legacyScope = store.GetLegacyReceiveContext("10.0.0.11");
                var legacy = store.ReceiveLegacyMessage(new("舊版訊息", null), legacyScope, () => true);
                Check(!store.DeleteLegacyMessage(new(null, "10.0.0.12"), legacy.LegacySeq!.Value).Deleted && store.DeleteLegacyMessage(legacyScope.Scope, legacy.LegacySeq.Value).Deleted, "legacy deletion is scoped and persisted");

                var file = new NekoChat.Core.Attachments.AttachmentFile(Guid.NewGuid().ToString("N"), "附件.txt", 1, new string('a', 64));
                string attachmentId = Guid.NewGuid().ToString("D");
                var offered = store.ReceiveAttachmentOffer("10.0.0.11", 8768, attachmentId, file, "附件說明", new string('b', 64), () => true).Record!;
                Check(store.DeleteClassroomMessage(principal, offered.LocalSeq).Deleted && store.GetAttachment(offered.LocalSeq) is null && !store.AttachmentCurrent(offered), "attachment metadata deletion invalidates active transfer callbacks");
                Check(store.ReceiveAttachmentOffer("10.0.0.11", 8768, attachmentId, file, "附件說明", new string('b', 64), () => true).Record is null, "late attachment offer cannot restore deleted message");
                var attachmentDraftState = store.GetClassroomDraftState(principal);
                store.SaveClassroomDraft(principal, "保留新附件", attachmentDraftState.Epoch, attachmentDraftState.Revision + 1);
                var attachmentAttempt = (AttemptStarted)store.BeginClassroomSend(principal, "保留新附件", attachmentDraftState.Epoch, attachmentDraftState.Revision + 1);
                store.CompleteClassroomAttempt(attachmentAttempt.MessageId, new FailedOutcome("offline"), null, 1);
                store.SaveAttachmentDraft(principal, file, attachmentDraftState.Epoch);
                Check(store.DeleteClassroomPendingMessage(principal, attachmentAttempt.MessageId, 1).Deleted && store.GetAttachmentDraft(principal)?.File == file && store.GetClassroomDraftState(principal).RawText == "保留新附件", "deleting old text failure never orphans newly staged attachment or caption");
                store.ClearClassroomConversation(principal);

                var added = store.SaveManualRosterEntry(null, new("同學", "01", false, "10.0.0.21"), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                Check(added.Changed, "nickname fixture");
                var row = store.GetRosterSnapshot().Single().Entry;
                Check(store.SaveManualRosterEntry(row.Id, new("本機綽號", row.StudentNumber, row.IsTeacher, row.Endpoint), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).Changed, "existing nickname API remains usable");
            }
            using (var pages = ChatStore.Open(new(Path.Combine(root, "pagination"))))
            {
                for (int i = 1; i <= 205; i++)
                {
                    string text = "failed " + i;
                    pages.SaveClassroomDraft(principal, text, 0, i);
                    var attempt = (AttemptStarted)pages.BeginClassroomSend(principal, text, 0, i);
                    pages.CompleteClassroomAttempt(attempt.MessageId, new FailedOutcome("offline"), null, 1);
                }
                var recent = pages.ListClassroomPendingMessages(principal);
                var older = pages.ListClassroomPendingMessages(principal, 200, recent[0].Sequence);
                Check(recent.Count == 200 && older.Count == 5 && recent.Concat(older).Select(p => p.Attempt.MessageId).Distinct().Count() == 205, "pending keyset pages retain more than 200 failures without overlap");
                Check(older[0].Attempt.RawSnapshot == "failed 1", "oldest failure remains available for manual retry or deletion");
            }
            using (var store = ChatStore.Open(new(root)))
            {
                Check(store.ListClassroomMessages(principal).Items.Count == 0 && store.ListClassroomPendingMessages(principal).Count == 0 && store.GetClassroomDraftState(principal).RawText is null, "deletions survive restart");
                Check(store.ListLegacyMessages(new(null, "10.0.0.11")).Items.Count == 0 && store.ListClassroomMessages(other).Items.Single().RawText == "另一個對話", "legacy deletion and other history survive restart");
                var row = store.GetRosterSnapshot().Single().Entry;
                Check(row is { DisplayName: "本機綽號", StudentNumber: "01", Endpoint: "10.0.0.21", IsTeacher: false }, "nickname survives restart without identity/IP changes");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Message recovery/deletion: {count} real SQLite checks passed.");
    }
}
