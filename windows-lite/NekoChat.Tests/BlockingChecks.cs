using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Attachments;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.ClassroomFiles;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using ClassroomRoute=NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;
internal static class BlockingChecks
{
    const string A="10.40.0.1",B="10.40.0.2";
    static int count;
    static void Check(bool v,string m){count++;if(!v)throw new InvalidOperationException(m);}
    sealed class Faults:IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op){} public void BeforeCommit(string op)=>Before?.Invoke(op); public void AfterCommit(string op){}
    }
    sealed class Env:IDisposable
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"neko-block-"+Guid.NewGuid().ToString("N"));
        internal ChatStore Store;
        internal ChatStoreOptions Options;
        internal Env(int version=15,Faults? faults=null){Options=new(Root){Faults=faults};Store=ChatStore.Open(Options with{Migrations=Migration.Default.Take(version).ToArray()});}
        internal void Reopen(){Store.Dispose();Store=ChatStore.Open(Options);}
        internal long Number(string sql){using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Store.DatabasePath,Pooling=false}.ToString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());}
        public void Dispose(){Store.Dispose();Directory.Delete(Root,true);}
    }
    static string Token(ChatStore s)=>RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot());
    static string Add(ChatStore s,string ip,bool teacher=false)=>s.SaveManualRosterEntry(null,new("alias "+ip,null,teacher,ip),Token(s)).EntryId!;
    static ClassroomStored Receive(ChatStore s,string ip,string? id=null)=>(ClassroomStored)s.ReceiveClassroomMessage(ip,new(id??Guid.NewGuid().ToString("D"),"body"),()=>true);
    static string Queue(ChatStore s,ClassroomPrincipalKey p,string text){var d=s.GetClassroomDraftState(p);s.SaveClassroomDraft(p,text,d.Epoch,d.Revision+1);return s.EnqueueClassroomMessage(p,text,d.Epoch,d.Revision+1,8768);}
    sealed class Secrets:IShareSecrets {public byte[] Protect(byte[] b)=>b.ToArray();public byte[] Unprotect(byte[] b)=>b.ToArray();}
    public static async Task Run()
    {
        count=0;
        using(var e=new Env()){
            string id=Add(e.Store,A);var rx=Receive(e.Store,A);e.Store.SetSourceBlocked(A,true,Token(e.Store));
            var row=e.Store.GetClassroomInboxSnapshot().Entries.Single();
            Check(row.Blocked&&!row.CanSend&&row.Principal==rx.Principal&&e.Store.GetRosterSnapshot().Single().Entry.Id==id,"history/contact retained as blocked read-only");
            e.Reopen();Check(e.Store.IsSourceBlocked(A)&&e.Store.GetBlockedContacts().Single().DisplayName=="alias "+A,"block and alias survive restart");
            Console.WriteLine("PASS BK-01 block preserves history and persistent read-only projection (Core)");
        }
        using(var e=new Env()){
            e.Store.SetSourceBlocked(A,true);var sink=new ClassroomStoreSink(e.Store);int notices=0;sink.MessageStored+=(_,_,_)=>notices++;
            Check(e.Store.ReceiveClassroomMessage(A,new("blocked","body","wave"),()=>true) is ClassroomRejected,"text/reaction refused");
            Check(!e.Store.ReceiveLegacyMessage(new("legacy",null),e.Store.GetLegacyReceiveContext(A),()=>true).Stored,"legacy refused");
            var file=new AttachmentFile(Guid.NewGuid().ToString("N"),"file.txt",1,new string('a',64));bool rejected=false;
            try{e.Store.ReceiveAttachmentOffer(A,8768,Guid.NewGuid().ToString("D"),file,"",new string('b',64),()=>true);}catch(AttachmentException x){rejected=x.Code=="source_not_allowed";}
            Check(rejected&&!e.Store.ApplyClassroomReceipt(A,"receipt",RemoteReceiptKind.Read).Changed,"offer and receipt refused");
            var response=await sink.OnMessageAsync(A,B,new(ClassroomRequestKind.Message,MessageId:Guid.NewGuid().ToString("D"),Session:ClassroomPacketWriter.NewToken(),Text:"blocked"),()=>true,default);
            Check(response?.StatusCode==403&&notices==0,"existing error code and no notification");
            Check(e.Number("SELECT count(*) FROM ClassroomMessage")==0&&e.Number("SELECT count(*) FROM LegacyMessage")==0&&e.Number("SELECT count(*) FROM RosterEntry")==0&&e.Number("SELECT count(*) FROM ClassroomAutoSource")==0,"no content unread or automatic records");
            Console.WriteLine("PASS BK-02 blocked ingress stores and notifies nothing");
        }
        using(var e=new Env()){
            Add(e.Store,A);Add(e.Store,B);
            string Share(string ip){using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var f=new ShareManifest(ShareProtocol.Id(key),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"shared.txt",1,new string('a',64),ip,"",DateTime.UtcNow.ToString("O"));e.Store.MergeClassroomFile(ShareProtocol.Sign(f,"add",key));return f.Id;}
            string hidden=Share(A),visible=Share(B);e.Store.SetSourceBlocked(A,true);
            Check(e.Store.ListClassroomFiles().Select(r=>r.File.Id).SequenceEqual([visible])&&e.Store.GetClassroomFile(hidden) is null,"blocked author hidden including replicas");
            await using var attachments=new AttachmentService(e.Store,Path.Combine(e.Root,"files"),1);
            await using var shares=new ClassroomFileService(e.Store,attachments,new Secrets(),()=>[new(A,8768),new(B,8768)],()=>"10.40.0.99");shares.SetReady(true);
            byte[] request=JsonSerializer.SerializeToUtf8Bytes(new ShareRequest(1,"sync"));
            Check((await shares.HandleAsync("sync",request,A,()=>true,default)).StatusCode==403&&(await shares.HandleAsync("sync",request,B,()=>true,default)).StatusCode==200,"blocked node denied, other node usable");
            Check(Receive(e.Store,B).UnreadCount==1,"other contacts unaffected");
            Console.WriteLine("PASS BK-03 author and node filtering retains other peers");
        }
        using(var e=new Env()){
            Add(e.Store,A);Add(e.Store,B);var p=e.Store.ResolveClassroomPrincipal(A)!.Value;
            string id=Queue(e.Store,p,"preserved"),other=Queue(e.Store,e.Store.ResolveClassroomPrincipal(B)!.Value,"other");
            e.Store.ClaimOutbox(id);var active=e.Store.GetClassroomPreparedSend(id)!;e.Store.RecordClassroomRoute(id,active.TryCount,ClassroomRoute.Classroom,DateTime.UtcNow);
            e.Store.SetSourceBlocked(A,true);
            Check(e.Store.ListOutbox().Single(r=>r.MessageId==id) is{State:"cancelled",Error:"source_blocked"}&&e.Store.ListOutbox().Single(r=>r.MessageId==other).State=="queued","only blocked target stopped without delete");
            Check(!e.Store.RetryOutbox(id)&&e.Store.ClaimOutbox(id,true) is null&&!e.Store.OutboxCurrent(id),"no retry or active fence while blocked");
            Check(e.Store.CompleteClassroomAttempt(id,new RemoteStoredOutcome(DateTime.UtcNow,ChatStore.ClassroomFingerprint("preserved",null)),A,active.TryCount) is Discarded,"late ACK cannot revive blocked work");
            e.Store.SetSourceBlocked(A,false);Check(e.Store.ListOutbox().Single(r=>r.MessageId==id).State=="cancelled","unblock does not auto resume");
            Check(e.Store.RequeueClassroomAttempt(p,id,8768)&&e.Store.ClaimOutbox(id,true) is not null,"manual retry after unblock");
            Console.WriteLine("PASS BK-04 queued work stops without deletion or implicit resume");
        }
        using(var e=new Env()){
            Add(e.Store,A);var before=Receive(e.Store,A);e.Store.SetSourceBlocked(A,true);e.Store.ReceiveClassroomMessage(A,new("during","rejected"),()=>true);e.Store.SetSourceBlocked(A,false);
            Check(e.Store.GetClassroomInboxSnapshot().Entries.Single() is{Blocked:false,CanSend:true}&&e.Store.ListClassroomMessages(before.Principal).Items.Count==1,"unblock keeps previous history with no local backfill");
            Check(Receive(e.Store,A,"after").Principal==before.Principal,"new explicit send allowed after unblock");Console.WriteLine("PASS BK-05 unblock preserves alias/history without backfill");
        }
        using(var e=new Env()){
            string id=Add(e.Store,A);var first=Receive(e.Store,A);var preview=e.Store.PreviewRosterDelete(id);e.Store.DeleteRosterEntry(id,preview.SnapshotToken,preview.ToConfirmation());
            Check(Receive(e.Store,A).Principal==first.Principal,"removal allows automatic restore");e.Store.SetSourceBlocked(A,true);
            Check(e.Store.ReceiveClassroomMessage(A,new("no-restore","blocked"),()=>true) is ClassroomRejected,"block does not allow restore");Console.WriteLine("PASS BK-06 removal and block remain distinct");
        }
        using(var e=new Env()){
            string id=Add(e.Store,A,true);e.Store.SetSourceBlocked(A,true);
            Check(e.Store.GetClassroomTeacherIp() is null&&!e.Store.GetRosterSnapshot().Single().Entry.IsTeacher,"blocking clears teacher atomically");
            Check(e.Store.SetClassroomTeacher(id,Token(e.Store)).Reason=="blocked","blocked teacher cannot be re-promoted");e.Store.SetSourceBlocked(A,false);Check(e.Store.GetClassroomTeacherIp() is null,"unblock does not restore teacher");Console.WriteLine("PASS BK-07 block demotes teacher (Core)");
        }
        using(var e=new Env(14)){
            Add(e.Store,A);Receive(e.Store,A);e.Reopen();Check(e.Number("PRAGMA user_version")==15&&e.Store.GetBlockedContacts().Count==0&&e.Store.ListBackups().Any(b=>b.FileName.Contains("-v14-",StringComparison.Ordinal)),"v14 to v15 backup with no invented blocks");
        }
        var faults=new Faults();using(var e=new Env(faults:faults)){
            using var other=ChatStore.Open(e.Options with{Faults=null});using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            faults.Before=op=>{if(op=="contact-block"){entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();}};
            var blocking=Task.Run(()=>e.Store.SetSourceBlocked(A,true));Check(entered.Wait(TimeSpan.FromSeconds(5)),"block transaction reached commit fence");
            var incoming=Task.Run(()=>other.ReceiveClassroomMessage(A,new("racing","body"),()=>true));await Task.Delay(80);release.Set();await blocking;
            Check(await incoming is ClassroomRejected&&e.Number("SELECT count(*) FROM ClassroomMessage")==0&&e.Number("SELECT count(*) FROM RosterEntry")==0,"cross-connection receive observes committed block");
            faults.Before=null;
        }
        Console.WriteLine("PASS BK-08 upgrade backup and cross-connection atomic admission");
        Console.WriteLine($"Blocking: {count} assertions passed; WPF/physical acceptance not run.");
    }
}
