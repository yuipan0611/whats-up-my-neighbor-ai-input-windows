using NekoChat.Core.Attachments;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class TeacherFileChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Teacher files: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "teacher-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            string teacherId;
            using (var store = ChatStore.Open(new(root)))
            {
                string Add(string ip, bool teacher) => store.SaveManualRosterEntry(null, new(ip, null, teacher, ip), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).EntryId!;
                teacherId = Add("192.168.1.10", true); Add("192.168.1.20", false);
                AttachmentRecord Receive(string ip, string name) => store.ReceiveAttachmentOffer(ip, 8768, Guid.NewGuid().ToString("D"),
                    new(Guid.NewGuid().ToString("N"), name, 12, new string('a', 64)), "教材", AttachmentLimits.Token(), () => true).Record!;
                var first = Receive("192.168.1.10", "第一份.pdf");
                Receive("192.168.1.20", "同學.zip"); Receive("192.168.1.30", "名單外.png");
                var second = Receive("192.168.1.10", "第二份.py");
                var third = Receive("192.168.1.10", "第三份.zip");
                var principal = ClassroomPrincipalKey.ForRosterEntry(teacherId);
                var draft = store.GetClassroomDraftState(principal);
                Check(store.SaveAttachmentDraft(principal, new(Guid.NewGuid().ToString("N"), "送給老師.txt", 1, new string('b', 64)), draft.Epoch), "outgoing draft fixture");
                store.PrepareAttachmentSend(principal, "", 8768, draft.Epoch, draft.Revision);
                var page = store.ListTeacherFiles(pageSize: 2);
                Check(page.Items.Select(x => x.Attachment.LocalSeq).SequenceEqual(new[] { third.LocalSeq, second.LocalSeq }), "only incoming teachers, newest first");
                Check(page.NextBeforeLocalSeq == second.LocalSeq, "bounded page cursor");
                Check(store.ListTeacherFiles(page.NextBeforeLocalSeq, 2).Items.Single().Attachment.LocalSeq == first.LocalSeq, "older page without duplicate");
                Check(store.ListTeacherFiles().Items.Count == 3, "exclude outgoing, students and unknown IP");
                Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.RosterEntryId == teacherId).ClassroomUnread == 3, "listing files does not mark chat read");
                var old = store.GetRosterSnapshot().Single(x => x.Entry.Id == teacherId).Entry;
                store.SaveManualRosterEntry(teacherId, new("本機老師綽號", old.StudentNumber, true, old.Endpoint), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                Check(store.ListTeacherFiles().Items.All(x => x.TeacherName == "本機老師綽號"), "uses current local nickname");
                Check(store.SetAttachmentState(second, "failed", "storage_full"), "failure fixture");
                Check(store.ListTeacherFiles().Items.Single(x => x.Attachment.LocalSeq == second.LocalSeq).Attachment.State == "failed", "failure state is shared, not copied");
            }
            using (var store = ChatStore.Open(new(root)))
            {
                Check(store.ListTeacherFiles().Items.Count == 3, "restart restores index");
                var old = store.GetRosterSnapshot().Single(x => x.Entry.Id == teacherId).Entry;
                store.SaveManualRosterEntry(teacherId, new(old.DisplayName, old.StudentNumber, false, old.Endpoint), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                Check(store.ListTeacherFiles().Items.Count == 0, "removed teacher label removes index entries");
                store.SaveManualRosterEntry(teacherId, new(old.DisplayName, old.StudentNumber, true, old.Endpoint), RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                store.ClearClassroomConversation(ClassroomPrincipalKey.ForRosterEntry(teacherId));
                Check(store.ListTeacherFiles().Items.Count == 0, "clear removes index without resurrection");
                foreach (var size in new[] { 0, 201 })
                { bool rejected = false; try { store.ListTeacherFiles(pageSize: size); } catch (ArgumentOutOfRangeException) { rejected = true; } Check(rejected, "reject unbounded page size"); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Teacher files: {checks} real SQLite filtering, paging, state, clear and restart checks.");
    }
}
