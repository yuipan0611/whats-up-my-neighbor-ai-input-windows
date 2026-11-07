using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Attachments;
using NekoChat.Core.ClassroomFiles;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.Diagnostics;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;

internal static class ContactExperienceChecks
{
    private const string A = "10.20.0.1", B = "10.20.0.2", C = "10.20.0.3", Own = "10.20.0.99";
    private static int _checks;
    private static void Check(bool ok, string message) { _checks++; if (!ok) throw new InvalidOperationException(message); }
    private static string Token(ChatStore s) => RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot());
    private static string Add(ChatStore s, string ip, string? name = null, bool teacher = false)
    {
        var r = s.SaveManualRosterEntry(null, new(name ?? ip, null, teacher, ip), Token(s));
        Check(r.Changed, "manual contact saved: " + r.Reason); return r.EntryId!;
    }
    private static ClassroomPrincipalKey P(ChatStore s, string ip) => s.ResolveClassroomPrincipal(ip)!.Value;
    private static ClassroomStored Receive(ChatStore s, string ip, string? id = null, string text = "hello") =>
        (ClassroomStored)s.ReceiveClassroomMessage(ip, new(id ?? Guid.NewGuid().ToString("D"), text), () => true);
    private static string Queue(ChatStore s, ClassroomPrincipalKey p, string text)
    {
        var d = s.GetClassroomDraftState(p);
        Check(s.SaveClassroomDraft(p, text, d.Epoch, d.Revision + 1).Saved, "draft saved");
        return s.EnqueueClassroomMessage(p, text, d.Epoch, d.Revision + 1, 8768);
    }
    private static void Mark(ChatStore s, ClassroomPrincipalKey p)
    {
        var ids = s.ListClassroomMessages(p, pageSize: 200).Items.Where(x => x.Direction == "incoming").Select(x => x.LocalSeq).ToArray();
        s.MarkClassroomVisibleRead(p, ids, new(p.Value, true, true, false, true));
    }
    private sealed class Faults : IStoreFaults
    {
        internal string? Fail;
        public void AfterBegin(string op) { if (op == Fail) throw new IOException("synthetic local failure"); }
        public void BeforeCommit(string op) { if (Fail == "commit:" + op) throw new IOException("synthetic commit failure"); }
        public void AfterCommit(string op) { }
        public void BeforeBackup(string purpose) { if (Fail == "backup") throw new IOException("synthetic backup failure"); }
    }
    private sealed class Env : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-contacts-" + Guid.NewGuid().ToString("N"));
        internal ChatStore Store { get; private set; }
        internal ChatStoreOptions Options { get; }
        internal Env(int version = 14, Faults? faults = null, ClassroomOffRosterLimits? limits = null, Action<string>? diagnostic = null, DiagnosticJournal? journal = null)
        {
            Options = new(Root) { Faults = faults, OffRoster = limits ?? new(), DiagnosticSink = diagnostic, Diagnostics = journal };
            Store = ChatStore.Open(Options with { Migrations = Migration.Default.Take(version).ToArray() });
        }
        internal void Upgrade(string? teacher = null) { Store.Dispose(); Store = ChatStore.Open(Options with { LegacyTeacherIp = teacher, Migrations = Migration.Default.Take(14).ToArray() }); }
        internal object? Scalar(string sql)
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
        }
        internal long Count(string sql) => Convert.ToInt64(Scalar(sql));
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed record Scenario(string Name, Func<Task> Run);
    private static Scenario Sync(string name, Action action) => new(name, () => { action(); return Task.CompletedTask; });

    public static async Task Run(string? group = null)
    {
        _checks = 0;
        Scenario[] scenarios = [
            Sync("CT-13 v14 inbox projects active queued unknown and archived rows without writes", () => {
                using var e=new Env();string a=Add(e.Store,A,teacher:true);Receive(e.Store,A);
                e.Store.ReceiveLegacyMessage(new("legacy",null),e.Store.GetLegacyReceiveContext(A),()=>true);
                Receive(e.Store,B);string queue=Queue(e.Store,P(e.Store,B),"pending");
                e.Store.SaveClassroomDraft(ClassroomPrincipalKey.ForSourceIp(C),"unknown draft",0,1);
                var preview=e.Store.PreviewRosterDelete(a);e.Store.DeleteRosterEntry(a,preview.SnapshotToken,preview.ToConfirmation());
                string Hash()=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(e.Store.DatabasePath+"-wal")));
                string before=Hash();var snapshot=e.Store.GetClassroomInboxSnapshot();
                Check(snapshot.Entries.Count==3 && snapshot.TotalUnread==3 && snapshot.Entries.Select(r=>r.Principal).Distinct().Count()==3,"v14 inbox counts each active/archive/unknown stream once");
                Check(snapshot.Entries.Single(r=>r.RosterEntryId==a) is {Removed:true,CanSend:false,LegacyUnread:1,ClassroomUnread:1},"archived teacher legacy and classroom stay read-only");
                Check(snapshot.Entries.Single(r=>r.Endpoint==B).Attempt?.MessageId==queue && snapshot.Entries.Single(r=>r.Endpoint==C).Draft.RawText=="unknown draft","queued and draft-only projections retained");
                Check(before==Hash(),"v14 inbox snapshot performs no writes");
            }),
            Sync("CT-02 normalization failure rolls back and retries on next startup", () => {
                var faults=new Faults();var codes=new List<string>();using var e=new Env(13,faults:faults,diagnostic:codes.Add);
                Receive(e.Store,A,"normalization","keep");Add(e.Store,A,"known");
                faults.Fail="commit:chat-ux-normalize";e.Upgrade();
                Check(codes.Contains("contact-normalization-failed") && e.Count("SELECT count(*) FROM ClassroomMessage WHERE principal LIKE 'ip:%'")==1,"startup survives failed normalization without moving data");
                Check(codes.All(c=>!c.Contains(A) && !c.Contains("keep")),"normalization diagnostic has no address or text");
                faults.Fail=null;e.Upgrade();
                Check(e.Store.GetClassroomInboxSnapshot().Entries.Count==1 && e.Store.ListClassroomMessages(P(e.Store,A)).Items.Single().RawText=="keep","next startup retries the merge successfully");
            }),
            Sync("CT-13 pre-v8 orphan conversation remains a read-only archive", () => {
                using var e=new Env(7);string id=Add(e.Store,A);var saved=Receive(e.Store,A,"orphan","old archive");
                e.Scalar("DELETE FROM RosterEntry");e.Upgrade();
                var row=e.Store.GetClassroomInboxSnapshot().Entries.Single();
                Check(row.Removed && !row.CanSend && row.DisplayName=="已移除的聯絡人" && row.Principal==saved.Principal,"pre-v8 orphan is named and readable in archive");
                Check(e.Store.ListClassroomMessages(row.Principal).Items.Single().RawText=="old archive","orphan content preserved");
            }),
            Sync("CT-01 automatic first contact and message commit together", () => {
                using var e = new Env(); var r = Receive(e.Store, A);
                var row = e.Store.GetRosterSnapshot().Single();
                Check(row is { Source: "auto", Entry.IsTeacher: false } && row.Entry.Endpoint == A && row.Entry.DisplayName == A, "automatic student labeled by source IP");
                Check(r.Principal == P(e.Store, A) && r.Principal.IsRoster && e.Store.GetClassroomInboxSnapshot().Entries.Count == 1, "single contact owns first message");
                var faults = new Faults { Fail = "commit:classroom-receive" };
                using var broken = new Env(faults: faults); bool failed = false;
                try { Receive(broken.Store, A); } catch (IOException) { failed = true; }
                Check(failed && broken.Store.GetRosterSnapshot().Count == 0 && broken.Count("SELECT count(*) FROM ClassroomMessage") == 0, "no contact or ACK-able result before atomic message commit");
                faults.Fail = null; Check(Receive(broken.Store, A).UnreadCount == 1, "retry after rollback succeeds");
                var stale = e.Store.ReceiveClassroomMessage(B, new("stale", "not stored"), () => false);
                Check(stale is ClassroomRejected && e.Store.GetRosterSnapshot().Count == 1, "stale listener cannot add a contact");
                using var legacy = new Env();
                Check(legacy.Store.ReceiveLegacyMessage(new("first legacy",null),legacy.Store.GetLegacyReceiveContext(A),()=>true).Stored && legacy.Store.GetRosterSnapshot().Single().Source=="auto", "legacy first ingress also auto-adds");
                Check(legacy.Store.GetClassroomInboxSnapshot().Entries.Single().Principal.IsRoster,"legacy message uses new contact scope");
                using var attachment = new Env();
                var file = new AttachmentFile(Guid.NewGuid().ToString("N"),"first.txt",1,new string('a',64));
                var offered=attachment.Store.ReceiveAttachmentOffer(A,8768,Guid.NewGuid().ToString("D"),file,"",new string('b',64),()=>true);
                Check(offered.Record is not null && attachment.Store.GetRosterSnapshot().Single().Source=="auto" && attachment.Store.GetClassroomInboxSnapshot().Entries.Single().Principal.IsRoster,"attachment-only first ingress auto-adds");
            }),
            Sync("CT-02 migrate all conversation ownership including v13 outbox", () => {
                using var e = new Env(13); var ip = ClassroomPrincipalKey.ForSourceIp(A);
                var incoming = Receive(e.Store, A, "old-in", "old text"); e.Store.SetReadReceiptPolicy(true); Mark(e.Store, ip);
                var legacy = e.Store.GetLegacyReceiveContext(A);
                Check(e.Store.ReceiveLegacyMessage(new("legacy",null), legacy, () => true).Stored, "legacy seed");
                var file = new AttachmentFile(Guid.NewGuid().ToString("N"), "a.txt", 4, new string('a', 64));
                e.Store.ReceiveAttachmentOffer(A, 8768, Guid.NewGuid().ToString("D"), file, "attachment", new string('b',64), () => true);
                string pending = Queue(e.Store, ip, "queued");
                var oldDraft = e.Store.GetClassroomDraftState(ip);
                e.Store.SaveClassroomDraft(ip, "newer draft", oldDraft.Epoch, oldDraft.Revision + 1);
                e.Upgrade(); var r = Receive(e.Store, A, "new-in"); var p = r.Principal;
                Check(p.IsRoster && e.Store.GetClassroomInboxSnapshot().Entries.Count == 1, "one merged inbox subject");
                Check(e.Store.ListOutbox().Single().Principal == p && e.Store.ListOutbox().Single().TargetIp == A && e.Store.ListOutbox().Single().MessageId == pending, "outbox rebinding preserves ID and network target");
                Check(e.Store.GetClassroomDraftState(p).RawText == "newer draft" && e.Store.GetClassroomDraftState(ip).Epoch > oldDraft.Epoch, "draft preserved and stale source fence invalidated");
                Check(e.Store.ListPendingClassroomReceipts().Single().Principal == p, "read receipt moved");
                Check(e.Store.ListClassroomMessages(p).Items.Any(x => x.LocalSeq == incoming.LocalSeq && x.RawText == "old text"), "sequence/content preserved");
                Check(e.Count("SELECT count(*) FROM Attachment a JOIN ClassroomMessage m ON m.localSeq=a.localSeq WHERE m.principal='" + p.Value + "'") == 1, "attachment owner moved");
                Check(e.Store.ListLegacyMessages(new(p.RosterEntryId, null)).Items.Count == 1, "legacy scope moved");
                var claim = e.Store.ClaimOutbox(pending); Check(claim is not null, "merged queue still claimable");
                var active = e.Store.GetClassroomPreparedSend(pending)!;
                e.Store.RecordClassroomRoute(pending, active.TryCount, ClassroomRoute.Classroom, DateTime.UtcNow);
                Check(e.Store.ApplyClassroomReceipt(A, pending, RemoteReceiptKind.Read).Reason == "pending-local-confirmation", "read before ACK is retryable");
                Check(e.Store.CompleteClassroomAttempt(pending, new RemoteStoredOutcome(DateTime.UtcNow, ChatStore.ClassroomFingerprint("queued",null)), A, active.TryCount) is Confirmed, "ACK confirms merged owner");
                Check(e.Store.ApplyClassroomReceipt(A, pending, RemoteReceiptKind.Read).Changed, "retry read after ACK applies");
                using var manual = new Env(13); Receive(manual.Store, A); Queue(manual.Store, ip, "manual queue"); manual.Upgrade();
                string id = Add(manual.Store, A, "manual");
                Check(manual.Store.ListOutbox().Single().Principal.RosterEntryId == id && manual.Store.GetClassroomInboxSnapshot().Entries.Count == 1, "manual add also merges without obsolete consent");
                using var duplicate=new Env(13); var duplicateIp=ClassroomPrincipalKey.ForSourceIp(A);
                Receive(duplicate.Store,A,"duplicate","same text");duplicate.Store.SetReadReceiptPolicy(true);Mark(duplicate.Store,duplicateIp);
                Add(duplicate.Store,A,"already added");Receive(duplicate.Store,A,"duplicate","same text");duplicate.Upgrade();
                Check(duplicate.Store.ListClassroomMessages(P(duplicate.Store,A)).Items is {Count:1} messages && messages.Single().LocalReadAt is not null && duplicate.Store.GetClassroomInboxSnapshot().TotalUnread==0,"duplicate merge preserves local read evidence");
                Check(duplicate.Store.ListPendingClassroomReceipts().Single().Principal==P(duplicate.Store,A),"duplicate merge retains the unsent read receipt");
                using var conflicts=new Env(13,diagnostic:_=>{});
                Receive(conflicts.Store,A,"same-id","original");string contact=Add(conflicts.Store,A,"existing");Receive(conflicts.Store,A,"same-id","different");
                conflicts.Upgrade();
                Check(conflicts.Count("SELECT count(*) FROM ClassroomMessage")==2 && conflicts.Store.SetClassroomTeacher(contact,Token(conflicts.Store)).Reason=="merge_conflict","same id with different content is retained and promotion refused");
                using var drafts=new Env();string target=Add(drafts.Store,A);var source=ClassroomPrincipalKey.ForSourceIp(A);var dest=P(drafts.Store,A);
                drafts.Store.SaveClassroomDraft(source,"left",0,1);drafts.Store.SaveClassroomDraft(dest,"right",0,1);
                Check(drafts.Store.SetClassroomTeacher(target,Token(drafts.Store)).Reason=="merge_conflict" && drafts.Store.GetClassroomDraftState(source).RawText=="left" && drafts.Store.GetClassroomDraftState(dest).RawText=="right","two different drafts survive refused merge");
                drafts.Store.SaveClassroomDraft(source,"left send",0,2);drafts.Store.SaveClassroomDraft(dest,"right send",0,2);
                var left=(AttemptStarted)drafts.Store.BeginClassroomSend(source,"left send",0,2);
                var right=(AttemptStarted)drafts.Store.BeginClassroomSend(dest,"right send",0,2);
                Check(drafts.Store.SetClassroomTeacher(target,Token(drafts.Store)).Reason=="merge_conflict" && drafts.Count("SELECT count(*) FROM ClassroomAttempt WHERE attemptState='sending'")==2,"two active sends are refused, never superseded or lost");
            }),
            Sync("CT-03 no automatic authority or file privileges", () => {
                using var e = new Env(); Receive(e.Store, A); var row = e.Store.GetRosterSnapshot().Single();
                Check(!row.Entry.IsTeacher && row.Entry.PeerId is null && e.Count("SELECT count(*) FROM Peer") == 0 && e.Store.GetClassroomTeacherIp() is null, "no teacher, peer, key or trust created");
                Check(!e.Store.CanAutoReceiveAttachments(P(e.Store,A)) && e.Store.IsAutomaticContact(A), "automatic contact cannot trigger trusted attachment download");
                e.Store.SaveManualRosterEntry(row.Entry.Id, new("a local nickname", null, false, A), Token(e.Store));
                Check(e.Store.IsAutomaticContact(A) && !e.Store.CanAutoReceiveAttachments(P(e.Store,A)), "renaming is not a trust grant");
                Check(ClassroomNotificationNames.Format(A, "a local nickname") == "a local nickname", "notification is local label only");
            }),
            new("CT-04 concurrent and duplicate ingress is idempotent", async () => {
                using var e = new Env(); string id = Guid.NewGuid().ToString("D");
                var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => e.Store.ReceiveClassroomMessage(A, new(id,"same"), () => true))));
                Check(results.Count(x => x is ClassroomStored) == 1 && results.Count(x => x is ClassroomDuplicate) == 11, "exactly one store and duplicate responses");
                Check(e.Store.GetRosterSnapshot().Count == 1 && e.Count("SELECT count(*) FROM ClassroomMessage") == 1, "one contact and one message");
            }),
            Sync("CT-05 recoverable contact failure falls back without lying about storage", () => {
                var codes = new List<string>(); var faults = new Faults { Fail = "auto-contact" };
                using var e = new Env(faults: faults, diagnostic: codes.Add);
                var r = Receive(e.Store, A); Check(!r.Principal.IsRoster && e.Store.GetRosterSnapshot().Count == 0, "fallback ip conversation stored and replyable");
                Check(e.Store.GetClassroomInboxSnapshot().Entries.Single().CanSend && codes.Contains("auto-contact-failed") && codes.All(c => !c.Contains(A)), "fixed code, no source IP diagnostics");
                faults.Fail = null; var next = Receive(e.Store, A);
                Check(next.Principal.IsRoster && e.Store.ListClassroomMessages(next.Principal).Items.Count == 2, "next successful admission merges fallback history");
            }),
            new("CT-05 diagnostic sink and observer failures never block ingress or recurse", async () => {
                using var area = new Env(); await using var journal = new DiagnosticJournal(Path.Combine(area.Root,"diagnostics"),"0.1.0-beta.12");
                var faults = new Faults { Fail="auto-contact" }; int sinkCalls=0, observerCalls=0;
                using var e = new Env(faults:faults, diagnostic:_=>{sinkCalls++;throw new IOException("sensitive sink detail");},journal:journal);
                string id=Guid.NewGuid().ToString("D"), body="sensitive incoming body";
                var first=Receive(e.Store,A,id,body);
                Check(!first.Principal.IsRoster&&sinkCalls==1,"local admission fallback receives successfully despite one throwing diagnostic sink");
                Check(e.Store.GetRosterSnapshot().Count==0&&e.Store.ListClassroomMessages(first.Principal).Items.Single().RawText==body,"sink failure preserves body with no partial contact");
                Check(journal.Recent.Any(r=>r.Stage==DiagnosticStage.StorageWrite&&r.Code==DiagnosticCode.NotificationFailed),"sink failure records fixed journal diagnostics without invoking sink again");
                e.Store.ClassroomCommunicationSucceeded+=_=>{observerCalls++;throw new IOException("sensitive observer detail");};
                e.Store.RecordClassroomCommunicationSuccess(A);
                Check(observerCalls==1&&sinkCalls==1&&journal.Recent.Any(r=>r.Stage==DiagnosticStage.ReceiveNotify&&r.Code==DiagnosticCode.NotificationFailed),"observer exception is isolated and does not recurse to diagnostic sink");
                faults.Fail=null;var next=Receive(e.Store,A);
                Check(next.Principal.IsRoster&&e.Store.ListClassroomMessages(next.Principal).Items.Count==2&&sinkCalls==1,"same session continues normal receive and merges preserved fallback");
                string metadata=JsonSerializer.Serialize(journal.Recent);
                Check(journal.Recent.All(r=>r.MessageId is null)&&new[]{A,id,body,"sensitive sink detail","sensitive observer detail"}.All(secret=>!metadata.Contains(secret,StringComparison.Ordinal)),"failure metadata excludes IP, UUID, contents and raw exception messages");
            }),
            Sync("CT-05 conflict after partial automatic insert rolls the whole contact back", () => {
                var codes = new List<string>(); var faults = new Faults();
                using var e = new Env(faults: faults, diagnostic: codes.Add);
                string id = Add(e.Store, A, "alias"); Receive(e.Store, A, "same-id", "original");
                var preview = e.Store.PreviewRosterDelete(id); Check(e.Store.DeleteRosterEntry(id, preview.SnapshotToken, preview.ToConfirmation()).Changed, "removed");
                faults.Fail = "auto-contact"; var parked = Receive(e.Store, A, "same-id", "different");
                Check(!parked.Principal.IsRoster, "fault parks a same-id message with different content under the source address");
                faults.Fail = null; var later = Receive(e.Store, A, "later-id", "later");
                Check(codes.Count(c => c == "auto-contact-failed") == 2, "restore conflict reported with the fixed code");
                Check(!later.Principal.IsRoster && e.Store.GetRosterSnapshot().Count == 0, "no phantom contact survives the failed restore");
                Check(e.Count("SELECT count(*) FROM RosterRemoved WHERE rosterEntryId='" + id + "'") == 1 && e.Count("SELECT count(*) FROM ClassroomAutoSource") == 0, "archive record and source budget untouched by the rolled-back restore");
                Check(e.Store.ListClassroomMessages(later.Principal).Items.Count == 2 && e.Store.ListClassroomMessages(ClassroomPrincipalKey.ForRosterEntry(id)).Items.Single().RawText == "original", "both histories keep their own messages");
            }),
            Sync("CT-06 automatic contacts retain source and content budgets", () => {
                using var e = new Env(limits: new(2, 2, 4, 100)); Receive(e.Store,A,text:"a"); Receive(e.Store,B,text:"b");
                Check(e.Store.ReceiveClassroomMessage(C,new("third","c"),()=>true) is ClassroomRejected { Reason: "offroster-limit" }, "third automatic source rejected");
                Receive(e.Store,A,text:"a2");
                Check(e.Store.ReceiveClassroomMessage(A,new("too-many","a3"),()=>true) is ClassroomRejected, "auto label does not bypass per-source cap");
                Add(e.Store,C); Check(Receive(e.Store,C).Principal.IsRoster, "manual contacts remain outside automatic quota");
                using var bytes = new Env(limits: new(2,1000,5000,3)); Receive(bytes.Store,A,text:"a");
                Check(bytes.Store.ReceiveClassroomMessage(A,new("bytes","long"),()=>true) is ClassroomRejected, "byte cap still enforced after promotion");
            }),
            Sync("CT-07 removed sender restores the same student contact", () => {
                using var e = new Env(); string id = Add(e.Store,A,"my alias",true); Receive(e.Store,A);
                var p = P(e.Store,A); Queue(e.Store,p,"queued before removal");
                var preview = e.Store.PreviewRosterDelete(id); Check(e.Store.DeleteRosterEntry(id,preview.SnapshotToken,preview.ToConfirmation()).Changed,"removed");
                var r = Receive(e.Store,A); var row=e.Store.GetRosterSnapshot().Single();
                Check(row.Entry.Id==id && row.Entry.DisplayName=="my alias" && !row.Entry.IsTeacher && row.Entry.PeerId is null,"restore alias/id but no teacher or identity");
                Check(r.Principal==p && e.Store.GetClassroomInboxSnapshot().Entries.Count==1 && e.Store.ListOutbox().Single().TargetIp==A,"history and original target preserved");
            }),
            new("CT-08 automatic contact confirmation is explicit and does not authenticate identity", async () => {
                using var e=new Env();Receive(e.Store,A);var row=e.Store.GetRosterSnapshot().Single();
                await using var files=new AttachmentService(e.Store,Path.Combine(e.Root,"files"),1);
                await using var shares=new ClassroomFileService(e.Store,files,new PlainSecrets(),()=>[new SharePeer(A,8768)],()=>Own);shares.SetReady(true);
                byte[] request=JsonSerializer.SerializeToUtf8Bytes(new ShareRequest(1,"sync"));
                Check((await shares.HandleAsync("sync",request,A,()=>true,default)).StatusCode==403,"automatic source cannot access classroom catalog even if adapter lists it");
                string token=Token(e.Store);
                e.Store.SaveManualRosterEntry(row.Entry.Id,new("local alias",null,false,B),token);
                Check(e.Store.IsAutomaticContact(B) && !e.Store.CanAutoReceiveAttachments(P(e.Store,B)),"IP/name edits keep automatic restrictions");
                Check(!e.Store.ConfirmAutomaticContact(row.Entry.Id,token).Changed,"stale confirmation rejected");
                Check(e.Store.ConfirmAutomaticContact(row.Entry.Id,Token(e.Store)).Changed && !e.Store.IsAutomaticContact(B) && e.Store.CanAutoReceiveAttachments(P(e.Store,B)),"explicit confirmation adopts ordinary contact policy");
                Check(e.Store.GetRosterSnapshot().Single().Entry is {IsTeacher:false,PeerId:null} && e.Count("SELECT count(*) FROM Peer")==0,"confirmation does not create identity or teacher role");
                Check(e.Count("SELECT count(*) FROM ClassroomAutoSource")==0,"confirmation releases automatic source slots after an IP edit");
                Check(e.Store.GetRosterSnapshot().Single().Entry.Id==row.Entry.Id && e.Store.ListClassroomMessages(P(e.Store,B)).Items.Count==1,"same ID and history survive confirmation/IP edit");
            }),
            Sync("CT-13 removal archives readable history without granting a send route", () => {
                using var e=new Env(); string id=Add(e.Store,A,"archived"); Receive(e.Store,A);
                var legacy=e.Store.GetLegacyReceiveContext(A); e.Store.ReceiveLegacyMessage(new("legacy archive",null),legacy,()=>true);
                var preview=e.Store.PreviewRosterDelete(id); Check(e.Store.GetRosterSnapshot().Count==1,"preview/cancel do not mutate");
                Check(e.Store.DeleteRosterEntry(id,preview.SnapshotToken,preview.ToConfirmation()).Changed,"confirmed removal");
                var entry=e.Store.GetClassroomInboxSnapshot().Entries.Single();
                Check(entry.Removed && !entry.CanSend && !entry.IsTeacher && entry.TotalUnread==2,"archived read-only preserves both unread streams");
                Check(e.Store.ListLegacyMessages(new(id,null)).Items.Count==1,"archived legacy remains readable under original owner");
            }),
            Sync("CT-13 automatic restore clears the archive so the inbox lists the contact once", () => {
                using var e=new Env(); string id=Add(e.Store,A,"restored"); Receive(e.Store,A,"m1","first");
                var preview=e.Store.PreviewRosterDelete(id); Check(e.Store.DeleteRosterEntry(id,preview.SnapshotToken,preview.ToConfirmation()).Changed,"confirmed removal");
                Check(e.Count("SELECT count(*) FROM RosterRemoved")==1 && e.Count("SELECT count(*) FROM ClassroomArchivedContact")==1 && e.Store.GetClassroomInboxSnapshot().Entries.Single().Removed,"removal records the archive and shows one removed row");
                Receive(e.Store,A,"m2","back");
                Check(e.Store.GetRosterSnapshot().Single().Entry.Id==id,"the same stranger address restores the same id");
                Check(e.Count("SELECT count(*) FROM RosterRemoved")==0 && e.Count("SELECT count(*) FROM ClassroomArchivedContact")==0,"restore clears RosterRemoved and the archive record (the precondition of the inbox removed-subject guard)");
                var entries=e.Store.GetClassroomInboxSnapshot().Entries;
                Check(entries.Count==1 && entries[0] is {Removed:false,CanSend:true,ClassroomUnread:2} && entries[0].RosterEntryId==id,"restored contact is listed once, active, with both messages");
            }),
            Sync("CT-13 inbox never lists a live roster row as removed even if a stale archive row exists", () => {
                // Defence in depth: no product path leaves RosterRemoved next to a live RosterEntry (see the restore scenario), so this state is forged with raw SQL.
                using var e=new Env(); string id=Add(e.Store,A,"live"); Receive(e.Store,A);
                e.Scalar("INSERT INTO RosterRemoved(rosterEntryId,displayName,removedUtc) VALUES('"+id+"','stale ghost','2026-11-01T00:00:00Z')");
                var entries=e.Store.GetClassroomInboxSnapshot().Entries;
                Check(entries.Count==1 && entries.Select(x=>x.Principal).Distinct().Count()==1 && entries[0] is {Removed:false,CanSend:true} && entries[0].DisplayName=="live","one active row, no duplicate removed row with the stale name");
            }),
            Sync("CT-13 a removed contact's own legacy history never leaks to an unknown source at the same address", () => {
                var faults=new Faults(); using var e=new Env(faults:faults); string id=Add(e.Store,A,"left");
                e.Store.ReceiveLegacyMessage(new("legacy of the removed contact",null),e.Store.GetLegacyReceiveContext(A),()=>true);
                var preview=e.Store.PreviewRosterDelete(id); Check(e.Store.DeleteRosterEntry(id,preview.SnapshotToken,preview.ToConfirmation()).Changed,"confirmed removal");
                var first=e.Store.GetClassroomInboxSnapshot().Entries;
                Check(first.Count==1 && first[0] is {Removed:true,LegacyUnread:1} && first[0].RosterEntryId==id,"orphaned row-scoped legacy history creates no unknown-source row");
                faults.Fail="auto-contact"; Receive(e.Store,A,"stranger","same address, auto-contact fails"); faults.Fail=null;
                var entries=e.Store.GetClassroomInboxSnapshot().Entries; var ip=entries.Single(x=>x.Principal.Equals(ClassroomPrincipalKey.ForSourceIp(A)));
                Check(entries.Count==2 && entries.Single(x=>x.RosterEntryId==id) is {Removed:true,LegacyUnread:1,ClassroomUnread:0},"removed row keeps its legacy history alone");
                Check(!ip.Removed && ip.LegacyUnread==0 && ip.ClassroomUnread==1 && ip.LegacyScope is {RosterEntryId:null},"unknown source at the same address shows only its own classroom message, no borrowed legacy history");
            }),
            Sync("CT-15 IP edits fence old routes and never infer multihomed identity", () => {
                using var e=new Env(); string id=Add(e.Store,A,"same person"); var p=P(e.Store,A); string queued=Queue(e.Store,p,"old target");
                var work=e.Store.ClaimOutbox(queued)!; var prepared=e.Store.GetClassroomPreparedSend(queued)!;
                e.Store.RecordClassroomRoute(queued,prepared.TryCount,ClassroomRoute.Classroom,DateTime.UtcNow);
                var d=e.Store.GetClassroomDraftState(p); e.Store.SaveClassroomDraft(p,"keep draft",d.Epoch,d.Revision+1);
                Check(e.Store.SaveManualRosterEntry(id,new("same person",null,false,B),Token(e.Store)).Changed,"edit IP");
                Check(e.Store.ListOutbox().Single().TargetIp==A && !e.Store.OutboxCurrent(queued),"queued bytes never silently rerouted");
                Check(e.Store.CompleteClassroomAttempt(queued,new RemoteStoredOutcome(DateTime.UtcNow,ChatStore.ClassroomFingerprint("old target",null)),A,prepared.TryCount) is Discarded,"late old ACK cannot confirm new target");
                Check(e.Store.GetClassroomDraftState(p).RawText=="keep draft","draft remains with contact");
                var old=Receive(e.Store,A); Check(old.Principal!=p && e.Store.GetRosterSnapshot().Count==2,"old address is a new contact, not a guessed identity");
                Receive(e.Store,C,text:"same person"); Check(e.Store.GetRosterSnapshot().Count==3,"same claimed text/name cannot merge another source interface");
            }),
            Sync("TC-01 promote existing teacher IP and merge its old conversation", () => {
                using var e=new Env(13); Receive(e.Store,A); string id=Add(e.Store,A,"teacher"); e.Upgrade();
                Check(e.Store.SetClassroomTeacherByIp(A,Token(e.Store)).Changed,"set existing teacher");
                Check(e.Store.GetRosterSnapshot().Single().Entry.Id==id && e.Store.GetClassroomTeacherIp()==A && e.Store.GetClassroomInboxSnapshot().Entries.Count==1,"same row pinned with history");
            }),
            Sync("TC-02 teacher replacement is atomic and keeps the old student", () => {
                using var e=new Env(); string a=Add(e.Store,A,"old",true),b=Add(e.Store,B,"new"); Receive(e.Store,A); Receive(e.Store,B);
                string stale=Token(e.Store); long removedBefore=e.Count("SELECT count(*) FROM RosterRemoved");
                Check(e.Store.SetClassroomTeacher(b,stale).Changed,"replace teacher");
                var oldEntry=e.Store.GetClassroomInboxSnapshot().Entries.Single(x=>x.RosterEntryId==a);
                Check(e.Count("SELECT count(*) FROM RosterRemoved")==removedBefore && !oldEntry.Removed && !oldEntry.IsTeacher && oldEntry.CanSend && oldEntry.TotalUnread==1,"replaced teacher is not archived: no removal record, not in the archive, still a sendable student with history");
                Check(!e.Store.GetRosterSnapshot().Single(r=>r.Entry.Id==a).Entry.IsTeacher && e.Store.GetRosterSnapshot().Count(r=>r.Entry.IsTeacher)==1,"old teacher remains ordinary contact");
                Check(!e.Store.SetClassroomTeacher(a,stale).Changed && e.Store.GetClassroomTeacherIp()==B,"stale confirmation cannot override newer choice");
            }),
            Sync("TC-02 database itself refuses a second teacher", () => {
                using var e=new Env(); string a=Add(e.Store,A,"first",true),b=Add(e.Store,B,"second");
                bool refused=false;
                try { e.Scalar("UPDATE RosterEntry SET isTeacher=1 WHERE id='"+b+"'"); } catch(SqliteException){ refused=true; }
                Check(refused && e.Count("SELECT count(*) FROM RosterEntry WHERE isTeacher=1")==1 && e.Store.GetClassroomTeacherIp()==A,"direct SQL cannot create two teachers");
                bool insertRefused=false;
                try { e.Scalar("INSERT INTO RosterEntry(id,displayName,isTeacher,endpoint,source) VALUES('"+Guid.NewGuid().ToString("N")+"','x',1,'"+C+"','manual')"); } catch(SqliteException){ insertRefused=true; }
                Check(insertRefused && e.Count("SELECT count(*) FROM RosterEntry WHERE isTeacher=1")==1,"direct insert of a second teacher is refused too");
            }),
            Sync("TC-03 SQLite alone owns the projected teacher address", () => {
                using var e=new Env(); string id=Add(e.Store,A);
                e.Store.SetClassroomTeacher(id,Token(e.Store)); Check(e.Store.GetClassroomTeacherIp()==A,"adapter receives authoritative projection");
                e.Upgrade(B); Check(e.Store.GetClassroomTeacherIp()==A,"reopen ignores stale JSON hint after migration");
            }),
            Sync("TC-04 clear teacher retains student history", () => {
                using var e=new Env(); string id=Add(e.Store,A,teacher:true); Receive(e.Store,A);
                e.Store.ClearClassroomTeacher(id,Token(e.Store));
                Check(e.Store.GetClassroomTeacherIp() is null && !e.Store.GetRosterSnapshot().Single().Entry.IsTeacher && e.Store.GetClassroomInboxSnapshot().Entries.Single().TotalUnread==1,"clear role keeps contact/history");
            }),
            Sync("TC-05 removed teacher is archived not a regular student", () => {
                using var e=new Env(); string id=Add(e.Store,A,teacher:true); Receive(e.Store,A);
                var preview=e.Store.PreviewRosterDelete(id); e.Store.DeleteRosterEntry(id,preview.SnapshotToken,preview.ToConfirmation());
                Check(e.Store.GetClassroomTeacherIp() is null && e.Store.GetRosterSnapshot().Count==0 && e.Store.GetClassroomInboxSnapshot().Entries.Single().Removed,"teacher archived only");
            }),
            Sync("TC-06 v13 upgrade backs up and normalizes teachers without data loss", () => {
                using var e=new Env(13); Receive(e.Store,A); string a=Add(e.Store,A,"one",true),b=Add(e.Store,B,"two",true);
                e.Upgrade(B); Check(e.Count("PRAGMA user_version")==14,"database records the v14 migration"); Check(e.Store.GetClassroomTeacherIp()==B && e.Store.GetRosterSnapshot().Count(r=>r.Entry.IsTeacher)==1,"legacy hint resolves multiple teachers");
                Check(e.Store.GetClassroomInboxSnapshot().Entries.Single(x=>x.RosterEntryId==a).TotalUnread==1,"teacher unknown history merged");
                var backup=e.Store.ListBackups().Single(x=>x.FileName.Contains("-v13-",StringComparison.Ordinal));
                using var conn=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(e.Root,"backup",backup.FileName),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
                conn.Open(); using var cmd=conn.CreateCommand(); cmd.CommandText="PRAGMA user_version";
                Check(Convert.ToInt64(cmd.ExecuteScalar())==13,"pre-upgrade backup remains version 13");
                cmd.CommandText="SELECT count(*) FROM RosterEntry WHERE isTeacher=1"; Check(Convert.ToInt64(cmd.ExecuteScalar())==2,"backup predates teacher normalization");
                using var blocked=new Env(13, faults:new Faults()); blocked.Store.Dispose();
                bool rejected=false; try { using var bad=ChatStore.Open(blocked.Options with {Faults=new Faults{Fail="backup"}}); } catch(StoreException){rejected=true;}
                Check(rejected,"backup failure stops upgrade");
            }),
            Sync("TC-07 CSV seed chooses only the first nonlocal teacher", () => {
                var codes=new List<string>(); using var e=new Env(diagnostic:codes.Add);
                byte[] csv=Encoding.UTF8.GetBytes("student_number,name,ip,role\n1,local,"+Own+",teacher\n2,first,"+A+",teacher\n3,second,"+B+",teacher\n");
                var preview=ClassroomRosterSeed.Preview(csv,e.Store.GetRosterSnapshot(),[Own])!;
                Check(e.Store.ApplyRosterPreview(preview).Applied,"CSV seed applied");
                Check(e.Store.GetClassroomTeacherIp()==A && e.Store.GetRosterSnapshot().Count==2 && codes.Contains("multiple-teachers-ignored"),"first teacher only and fixed diagnostic");
                var update=RosterImportPreview.Create(e.Store.GetRosterSnapshot(),RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip,role\n2,renamed,"+A+",student\n3,second,"+B+",teacher\n")));
                Check(e.Store.ApplyRosterPreview(update).Applied && e.Store.GetClassroomTeacherIp()==A,"ordinary import cannot silently replace or clear the selected teacher");
            }),
            Sync("RR-01 new installations enable receipts without claiming a manual choice", () => {
                using var e=new Env(); Check(e.Store.GetReadReceiptPolicy().Enabled && !e.Store.GetReadReceiptPreferences().ExplicitChoice,"default on, not explicit");
                Check(e.Store.GetReadReceiptPreferences().NoticeShown,"new installation does not claim it upgraded an old setting");
            }),
            Sync("RR-02 migration respects generations and persists one-time notice", () => {
                using var untouched=new Env(13); untouched.Upgrade();
                Check(untouched.Store.GetReadReceiptPolicy().Enabled && !untouched.Store.GetReadReceiptPreferences().NoticeShown,"legacy generation zero becomes on with pending notice");
                untouched.Store.AcknowledgeReadReceiptNotice(); untouched.Upgrade(); Check(untouched.Store.GetReadReceiptPreferences().NoticeShown,"notice shown persisted");
                using var disabled=new Env(13); disabled.Store.SetReadReceiptPolicy(true); disabled.Store.SetReadReceiptPolicy(false); disabled.Upgrade();
                Check(!disabled.Store.GetReadReceiptPolicy().Enabled && disabled.Store.GetReadReceiptPreferences().ExplicitChoice,"explicit old off retained");
                disabled.Store.SetReadReceiptPolicy(false); disabled.Upgrade(); Check(!disabled.Store.GetReadReceiptPolicy().Enabled,"no-op explicit off survives reopen");
            }),
            new("RR-03 valid same-peer communication rearms exhausted receipts", async () => {
                using var e=new Env(); Receive(e.Store,A); Mark(e.Store,P(e.Store,A));
                var clock=new ManualClock(); var http=new ReceiptHttp { Offline=true };
                var sender=new ClassroomSender(e.Store,new HttpClient(http)); await using var service=new ClassroomReceiptService(e.Store,sender,ownIp:Own,clock:clock);
                for(int i=0;i<8;i++){await service.ProcessOutboxBatchAsync();clock.Advance();}
                long attempts=service.TotalAttempts; await service.ProcessOutboxBatchAsync(); Check(attempts==8 && service.TotalAttempts==8,"receipt stops at session bound");
                http.Offline=false;
                var endpoint=new ClassroomEndpointAdapter(new ClassroomStoreSink(e.Store),capabilities:["msg.plain","receipt.read"]);endpoint.SetReady(true);
                var context=new ClassroomConnectionContext(A,Own);
                await endpoint.HandleAsync(new("classroom.hello",Encoding.UTF8.GetBytes("{}")),context,()=>true,default);
                await service.ProcessOutboxBatchAsync();Check(service.TotalAttempts==8,"invalid hello does not rearm");
                await endpoint.HandleAsync(new("classroom.hello",ClassroomPacketWriter.HelloRequest(ClassroomPacketWriter.NewToken())),new(B,Own),()=>true,default);
                await service.ProcessOutboxBatchAsync();Check(service.TotalAttempts==8,"other peer does not rearm");
                await endpoint.HandleAsync(new("classroom.hello",ClassroomPacketWriter.HelloRequest(ClassroomPacketWriter.NewToken())),context,()=>true,default);
                await service.ProcessOutboxBatchAsync();Check(service.TotalCompleted==1 && e.Store.ListPendingClassroomReceipts().Count==0,"same peer hello recovers without restart");
            }),
            new("RR-03 the receipt's own successful hello cannot reset its retry budget", async () => {
                using var e=new Env(); Receive(e.Store,A); Mark(e.Store,P(e.Store,A));
                var clock=new ManualClock(); var http=new ReceiptHttp { FailReceiptPost=true };
                http.OnHello=()=>e.Store.RecordClassroomCommunicationSuccess(A);   // a same-peer success reported while this receipt attempt is in flight
                var sender=new ClassroomSender(e.Store,new HttpClient(http)); await using var service=new ClassroomReceiptService(e.Store,sender,ownIp:Own,clock:clock);
                for(int i=0;i<20;i++){await service.ProcessOutboxBatchAsync();clock.Advance();}
                Check(service.TotalAttempts==8 && service.TotalCompleted==0 && e.Store.ListPendingClassroomReceipts().Count==1,"hello succeeds and reports success mid-flight, receipt POST keeps failing: attempts stop at the session bound of 8");
            }),
            new("RR-04 paging and per-target admission avoid head-of-line starvation", async () => {
                using var e=new Env(); for(int i=0;i<51;i++)Receive(e.Store,A); Receive(e.Store,B);Mark(e.Store,P(e.Store,A));Mark(e.Store,P(e.Store,B));
                var http=new ReceiptHttp { OfflineIp=A }; var sender=new ClassroomSender(e.Store,new HttpClient(http));
                await using var service=new ClassroomReceiptService(e.Store,sender,ownIp:Own);
                await service.ProcessOutboxBatchAsync();await service.ProcessOutboxBatchAsync();
                Check(http.Receipts.Contains(B) && service.TotalCompleted==1,"online peer beyond first fifty is served");
                Check(service.TotalAttempts<=3,"offline target attempted at most once per batch");
            }),
            new("RR-05 disabling drops unsent receipts and prevents historical backfill", async () => {
                using var e=new Env();Receive(e.Store,A);Mark(e.Store,P(e.Store,A));var old=e.Store.ListPendingClassroomReceipts().Single();
                Check(e.Store.SetReadReceiptPolicy(false).DroppedReceipts==1,"off drops queue");
                var http=new ReceiptHttp(); await using var service=new ClassroomReceiptService(e.Store,new(e.Store,new HttpClient(http)),ownIp:Own);
                e.Store.RecordClassroomCommunicationSuccess(A);await service.ProcessOutboxBatchAsync();Check(service.TotalAttempts==0,"communication never bypasses off policy");
                e.Store.SetReadReceiptPolicy(true);Mark(e.Store,P(e.Store,A));Check(e.Store.ListPendingClassroomReceipts().Count==0 && !e.Store.CompleteClassroomReadReceipt(old),"no backfill or stale policy completion");
            }),
            new("RR-05 sender gate refuses a stale listed receipt while disabled", async () => {
                using var e=new Env();Receive(e.Store,A);Mark(e.Store,P(e.Store,A));var old=e.Store.ListPendingClassroomReceipts().Single();
                var proxy=System.Reflection.DispatchProxy.Create<IChatStore,StalePendingStore>();var stale=(StalePendingStore)(object)proxy;stale.Inner=e.Store;stale.Row=old;
                var live=new ReceiptHttp();await using(var control=new ClassroomReceiptService(proxy,new(e.Store,new HttpClient(live)),ownIp:Own))
                { await control.ProcessOutboxBatchAsync(); Check(control.TotalAttempts==1 && live.Receipts.Contains(A),"control: while enabled the listed receipt is transmitted"); }
                Receive(e.Store,A,text:"second");Mark(e.Store,P(e.Store,A));old=e.Store.ListPendingClassroomReceipts().Single();stale.Row=old;
                string created=(string)e.Scalar("SELECT createdUtc FROM ClassroomReceiptOutbox")!;
                Check(e.Store.SetReadReceiptPolicy(false).DroppedReceipts==1,"off drops queue");
                e.Scalar("INSERT INTO ClassroomReceiptOutbox(originalLocalSeq,principal,receiptKind,createdUtc) VALUES("+old.OriginalLocalSeq+",'"+old.Principal.Value+"','read','"+created+"')");
                Check(e.Count("SELECT count(*) FROM ClassroomReceiptOutbox")==1 && e.Store.ListPendingClassroomReceipts().Count==0,"store itself never lists a queued receipt while disabled");
                var http=new ReceiptHttp(); await using var service=new ClassroomReceiptService(proxy,new(e.Store,new HttpClient(http)),ownIp:Own);
                e.Store.RecordClassroomCommunicationSuccess(A);await service.ProcessOutboxBatchAsync();await service.ProcessOutboxBatchAsync();
                Check(service.TotalAttempts==0 && http.Receipts.Count==0,"no hello or receipt reaches the network while disabled even if a stale row is listed");
            }),
            new("RR-05 receipt in flight across an off/on toggle is never transmitted", async () => {
                using var e=new Env(); Receive(e.Store,A); Mark(e.Store,P(e.Store,A));
                var http=new ReceiptHttp(); bool toggled=false;
                http.OnHello=()=>{ if(toggled)return; toggled=true; e.Store.SetReadReceiptPolicy(false); e.Store.SetReadReceiptPolicy(true); };
                await using var service=new ClassroomReceiptService(e.Store,new(e.Store,new HttpClient(http)),ownIp:Own);
                await service.ProcessOutboxBatchAsync();
                Check(toggled && e.Store.GetReadReceiptPolicy().Enabled && e.Store.GetReadReceiptPolicy().Generation>=2,"policy went off and on while the receipt was in flight");
                Check(http.Receipts.Count==0 && service.TotalCompleted==0,"old-generation receipt is not transmitted after the policy generation changed");
            }),
            Sync("MD-02 local deletion keeps remote data and dedup tombstones", () => {
                using var local=new Env();using var remote=new Env();string id=Guid.NewGuid().ToString("D");
                var received=Receive(local.Store,A,id,"same body");Receive(remote.Store,B,id,"same body");
                Check(local.Store.DeleteClassroomMessage(received.Principal,received.LocalSeq).Deleted,"local delete");
                Check(local.Store.ReceiveClassroomMessage(A,new(id,"same body"),()=>true) is ClassroomClearedDuplicate,"resend cannot resurrect");
                Check(remote.Count("SELECT count(*) FROM ClassroomMessage")==1 && local.Count("SELECT count(*) FROM ClassroomMessage")==0,"only local copy removed");
            }),
            Sync("MD-04 stop and delete is scoped atomic and rejects active work", () => {
                var faults=new Faults();using var e=new Env(faults:faults);string id=Add(e.Store,A);var p=P(e.Store,A);string a=Queue(e.Store,p,"first"),b=Queue(e.Store,p,"second");
                var d=e.Store.GetClassroomDraftState(p);e.Store.SaveClassroomDraft(p,"new draft",d.Epoch,d.Revision+1);
                var claimed=e.Store.ClaimOutbox(a)!;var work=e.Store.GetClassroomPreparedSend(a)!;
                Check(!e.Store.DeleteClassroomPendingMessage(p,a,work.TryCount).Deleted,"active transmission must stop first");
                e.Store.CancelOutbox(a);e.Store.CancelClassroomAttempt(a,work.TryCount);
                Check(e.Store.DeleteClassroomPendingMessage(p,a,work.TryCount+1).Reason=="stale-attempt","stale attempt cannot delete newer work");
                faults.Fail="commit:classroom-delete-pending";bool rolledBack=false;
                try{e.Store.DeleteClassroomPendingMessage(p,a,work.TryCount);}catch(IOException){rolledBack=true;}
                Check(rolledBack && e.Store.ListOutbox().Count==2 && e.Store.ListClassroomPendingMessages(p).Count==2,"failed commit rolls back both retry stop and deletion");
                faults.Fail=null;
                Check(e.Store.DeleteClassroomPendingMessage(p,a,work.TryCount).Deleted,"stopped item removed in transaction");
                e.Store.RecoverOutbox();Check(e.Store.ListOutbox().Single().MessageId==b && e.Store.GetClassroomDraftState(p).RawText=="new draft","other queue and newer draft survive");
                Check(!e.Store.RequeueClassroomAttempt(p,a,8768) && e.Store.CompleteClassroomAttempt(a,new RemoteStoredOutcome(DateTime.UtcNow,ChatStore.ClassroomFingerprint("first",null)),A) is Discarded,"deleted item cannot retry or revive via late ACK");
            })
        ];
        foreach(var scenario in scenarios.Where(s=>group is null || s.Name.StartsWith(group,StringComparison.Ordinal)))
        { await scenario.Run(); Console.WriteLine("PASS " + scenario.Name); }
        Console.WriteLine($"Contact experience: {_checks} assertions passed; WPF/physical acceptance not run.");
    }
    public class StalePendingStore : System.Reflection.DispatchProxy
    {
        internal IChatStore Inner = null!;
        internal PendingClassroomReceipt? Row;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IChatStore.ListPendingClassroomReceipts) && Row is { } row)
                return (args![1] as long?) is { } after && row.OriginalLocalSeq <= after ? Array.Empty<PendingClassroomReceipt>().AsReadOnly() : new[] { row }.AsReadOnly();
            try { return method.Invoke(Inner, args); } catch (System.Reflection.TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
    }
    private sealed class PlainSecrets : IShareSecrets { public byte[] Protect(byte[] b)=>b.ToArray(); public byte[] Unprotect(byte[] b)=>b.ToArray(); }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>_ticks;
        internal void Advance()=>_ticks+=1_000_000;
    }
    private sealed class ReceiptHttp : HttpMessageHandler
    {
        internal bool Offline;
        internal string? OfflineIp;
        internal bool FailReceiptPost;
        internal Action? OnHello;
        internal List<string> Receipts {get;}=[];
        private readonly string _session=ClassroomPacketWriter.NewToken();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            string ip=request.RequestUri!.Host;
            request.Options.Set(ClassroomSender.LocalEndpointOptionKey,Own);
            if(Offline || ip==OfflineIp)return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("unavailable")};
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellation));var json=doc.RootElement;
            byte[] bytes;
            if(request.RequestUri.AbsolutePath.EndsWith("/hello",StringComparison.Ordinal))
            { OnHello?.Invoke(); bytes=ClassroomPacketWriter.HelloOk(json.GetProperty("client_nonce").GetString()!,_session,ip,Own,["msg.plain","receipt.read"]); }
            else if(FailReceiptPost){ return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("unavailable")}; }
            else { Receipts.Add(ip);bytes=ClassroomPacketWriter.ReceiptAck("ok",json.GetProperty("message_id").GetString()!,_session,Own,ip); }
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        }
    }
}
