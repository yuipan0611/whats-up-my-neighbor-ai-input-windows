using System.Security.Cryptography;
using System.Net;
using System.IO.Compression;
using System.Text;
using NekoChat.Core.Diagnostics;
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
        internal Action<string>? Before, After, Read;
        public void AfterBegin(string op)=>After?.Invoke(op); public void AfterRead(string op)=>Read?.Invoke(op); public void BeforeCommit(string op)=>Before?.Invoke(op); public void AfterCommit(string op){}
    }
    sealed class Env:IDisposable
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"neko-block-"+Guid.NewGuid().ToString("N"));
        internal ChatStore Store;
        internal ChatStoreOptions Options;
        internal Env(int version=15,Faults? faults=null,DiagnosticJournal? diagnostics=null){Options=new(Root){Faults=faults,Diagnostics=diagnostics};Store=ChatStore.Open(Options with{Migrations=Migration.Default.Take(version).ToArray()});}
        internal void Reopen(){Store.Dispose();Store=ChatStore.Open(Options);}
        internal long Number(string sql){using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Store.DatabasePath,Pooling=false}.ToString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());}
        public void Dispose(){Store.Dispose();Directory.Delete(Root,true);}
    }
    static string Token(ChatStore s)=>RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot());
    static string Add(ChatStore s,string ip,bool teacher=false)=>s.SaveManualRosterEntry(null,new("alias "+ip,null,teacher,ip),Token(s)).EntryId!;
    static ClassroomStored Receive(ChatStore s,string ip,string? id=null)=>(ClassroomStored)s.ReceiveClassroomMessage(ip,new(id??Guid.NewGuid().ToString("D"),"body"),()=>true);
    static string Queue(ChatStore s,ClassroomPrincipalKey p,string text,int port=8768){var d=s.GetClassroomDraftState(p);s.SaveClassroomDraft(p,text,d.Epoch,d.Revision+1);return s.EnqueueClassroomMessage(p,text,d.Epoch,d.Revision+1,port);}
    sealed class Secrets:IShareSecrets {public byte[] Protect(byte[] b)=>b.ToArray();public byte[] Unprotect(byte[] b)=>b.ToArray();}
    sealed class Node : IAsyncDisposable
    {
        internal readonly DiagnosticJournal Journal;
        internal readonly ChatStore Store;
        internal readonly AttachmentService Files;
        internal readonly ClassroomTcpListener Listener;
        internal int Notices, Successes;
        internal Node(string root)
        {
            Journal = new(Path.Combine(root, "diagnostics"), "0.1.0-beta.12");
            Store = ChatStore.Open(new(Path.Combine(root,"data")){Diagnostics=Journal});
            Store.ClassroomCommunicationSucceeded += _ => Successes++;
            var sink = new ClassroomStoreSink(Store, Journal);
            sink.MessageStored += (_,_,_) => Notices++;
            sink.LegacyMessageStored += _ => Notices++;
            sink.ReceiptStored += (_,_) => Notices++;
            Files = new(Store,Path.Combine(root,"files"),1,diagnostics:Journal);
            Files.MessageStored += (_,_) => Notices++;
            var endpoint = new AttachmentEndpoint(new ClassroomEndpointAdapter(sink,capabilities:["msg.plain","receipt.read",AttachmentLimits.Capability],diagnostics:Journal),Files);
            Listener = new(endpoint,new(){BindAddress=IPAddress.Loopback,Port=0});
            Listener.Start();Files.Port=Listener.Port;
        }
        public async ValueTask DisposeAsync(){await Listener.DisposeAsync();await Files.DisposeAsync();Store.Dispose();await Journal.DisposeAsync();}
    }
    static string DatabaseSnapshot(string path)
    {
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());c.Open();
        using var q=c.CreateCommand();q.CommandText="PRAGMA user_version";string version=Convert.ToString(q.ExecuteScalar())!;
        q.CommandText="SELECT name,type,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' OR name='sqlite_sequence' ORDER BY name";
        var schema=new List<(string Name,string Type,string Sql)>();
        using(var r=q.ExecuteReader())while(r.Read())schema.Add((r.GetString(0),r.GetString(1),r.IsDBNull(2)?"":r.GetString(2)));
        var snapshot=new List<string>{version};
        foreach(var item in schema)
        {
            snapshot.Add(JsonSerializer.Serialize(item,new JsonSerializerOptions{IncludeFields=true}));
            if(item.Type!="table")continue;
            q.CommandText="SELECT * FROM \""+item.Name.Replace("\"","\"\"")+"\"";
            var rows=new List<string>();using(var r=q.ExecuteReader())while(r.Read())
            {var values=new object?[r.FieldCount];for(int i=0;i<values.Length;i++)values[i]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(JsonSerializer.Serialize(values));}
            snapshot.AddRange(rows.OrderBy(row=>row,StringComparer.Ordinal));
        }
        return string.Join("\n",snapshot);
    }
    static void V15FailureRollback()
    {
        using var e=new Env(14);string id=Add(e.Store,A,true);var received=Receive(e.Store,A);
        Queue(e.Store,received.Principal,"queued before upgrade");
        var draft=e.Store.GetClassroomDraftState(received.Principal);e.Store.SaveClassroomDraft(received.Principal,"newer draft",draft.Epoch,draft.Revision+1);
        e.Store.ReceiveLegacyMessage(new("legacy before upgrade",null),e.Store.GetLegacyReceiveContext(A),()=>true);
        string path=e.Store.DatabasePath;e.Store.Dispose();string before=DatabaseSnapshot(path);
        bool applied=false;
        var broken=Migration.V15 with{Apply=ctx=>{
            Migration.V15.Apply(ctx); // Real v15 DDL has already executed inside its transaction.
            ctx.Exec("INSERT INTO BlockedSource(ip,displayName,blockedUtc) VALUES(@ip,'partial',@now)",("@ip",A),("@now",ctx.NowUtc));
            ctx.Exec("UPDATE RosterEntry SET displayName='partial upgrade corruption'");
            applied=true;throw new IOException("synthetic halfway v15 failure");
        }};
        bool failed=false;
        try{using var unexpected=ChatStore.Open(e.Options with{Migrations=Migration.Default.Take(14).Append(broken).ToArray()});}
        catch(StoreException error){failed=error.Kind==StoreErrorKind.MigrationFailed&&error.FromVersion==14&&error.ToVersion==15;}
        Check(applied&&failed,"BK-08 F5 v15 fails after actual DDL and data mutations");
        Check(DatabaseSnapshot(path)==before,"BK-08 F5 failure restores v14 schema/version and all table contents exactly");
        using(var old=ChatStore.Open(e.Options with{Migrations=Migration.Default.Take(14).ToArray()}))
            Check(old.LatestVersion==14&&old.GetRosterSnapshot().Single().Entry.Id==id,"BK-08 F5 database reopens as valid v14 after failure");
        e.Reopen();
        Check(e.Store.LatestVersion==15&&e.Store.GetBlockedContacts().Count==0&&e.Store.GetClassroomDraftState(received.Principal).RawText=="newer draft"&&e.Store.ListOutbox().Single().State=="queued"&&e.Store.ListClassroomMessages(received.Principal).Items.Single().MessageId==received.MessageId,"BK-08 F5 next normal open upgrades successfully without corrupting data");
        Check(e.Store.ListBackups().Any(b=>b.FileName.Contains("-v14-",StringComparison.Ordinal)),"BK-08 F5 pre-upgrade v14 backup retained");
        Console.WriteLine("PASS BK-08 F5 real v15 partial-apply rollback, exact v14 data and successful retry");
    }
    static async Task BlockedAuthorRelay()
    {
        using var e=new Env();Add(e.Store,A);Add(e.Store,B);
        await using var attachments=new AttachmentService(e.Store,Path.Combine(e.Root,"files"),1);
        await using var shares=new ClassroomFileService(e.Store,attachments,new Secrets(),()=>[new(A,8768),new(B,8768)],()=>"10.40.0.99");shares.SetReady(true);
        byte[] bytes=Encoding.UTF8.GetBytes("cached author bytes \u4fdd\u7559\u5099\u63f4");
        var blob=await attachments.Vault.StageAsync(new MemoryStream(bytes),"replica.txt",bytes.Length);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest=new ShareManifest(ShareProtocol.Id(key),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),blob.Name,blob.Size,blob.Sha256,A,"",DateTime.UtcNow.ToString("O"));
        e.Store.MergeClassroomFile(ShareProtocol.Sign(manifest,"add",key));e.Store.SetClassroomFileLocal(manifest.Id,blob.BlobId,"available");
        e.Store.SetSourceBlocked(A,true);
        Check(shares.List().Count==0&&e.Store.GetClassroomFile(manifest.Id) is null,"BK-03 F3 blocked author remains hidden from local list/lookup");
        byte[] Request(string kind,string? grant=null)=>ShareProtocol.Bytes(new ShareRequest(1,kind,Id:manifest.Id,Offset:0,Count:bytes.Length,Token:grant));
        var status=await shares.HandleAsync("status",Request("status"),B,()=>true,default);
        var info=ShareProtocol.Parse<ShareStatus>(status.Body);
        Check(status.StatusCode==200&&info.Available&&info.Token.Length>0,"BK-03 F3 cached blocked-author replica remains available to another peer");
        var chunk=await shares.HandleAsync("chunk",Request("chunk",info.Token),B,()=>true,default);
        Check(chunk.StatusCode==200&&chunk.Binary&&chunk.Body.Span.SequenceEqual(bytes),"BK-03 F3 relay serves exact cached bytes despite local author block");
        Check((await shares.HandleAsync("chunk",Request("chunk",AttachmentLimits.Token()),B,()=>true,default)).StatusCode!=200,"BK-03 F3 relay still validates grant token");
        Check((await shares.HandleAsync("status",Request("status"),A,()=>true,default)).StatusCode==403,"BK-03 F3 blocked source still denied node access");
        e.Store.MergeClassroomFile(ShareProtocol.Sign(manifest,"withdraw",key));
        var withdrawn=await shares.HandleAsync("status",Request("status"),B,()=>true,default);
        Check(!ShareProtocol.Parse<ShareStatus>(withdrawn.Body).Available&&(await shares.HandleAsync("chunk",Request("chunk",info.Token),B,()=>true,default)).StatusCode!=200,"BK-03 F3 signed withdrawal still stops status/chunks with an old grant");
        Console.WriteLine("PASS BK-03 F3 relay cached blocked-author bytes, hide locally and honor withdrawal/grants");
    }
    static async Task D30FailurePrivacy()
    {
        using var root=new Env();
        await using var journal=new DiagnosticJournal(Path.Combine(root.Root,"diagnostics"),"0.1.0-beta.12");
        var faults=new Faults();using var e=new Env(faults:faults,diagnostics:journal);
        e.Store.SetSourceBlocked(A,true);
        faults.After=op=>{if(op=="classroom-receive")throw new StoreException(StoreErrorKind.DiskFull,"SENSITIVE blocked write");};
        var sink=new ClassroomStoreSink(e.Store,journal);
        var request=new ClassroomRequest(ClassroomRequestKind.Message,MessageId:Guid.NewGuid().ToString("D"),Session:ClassroomPacketWriter.NewToken(),Text:"SENSITIVE blocked text");
        bool failed=false;try{await sink.OnMessageAsync(A,B,request,()=>true,default);}catch(StoreException){failed=true;}
        Check(failed&&journal.Recent.All(r=>r.MessageId is null)&&e.Number("SELECT count(*) FROM ClassroomMessage")==0,"BK-02 D-30 failed blocked writes retain root cause without identifiers");
        faults.Read=_=>throw new IOException("SENSITIVE unreadable block state");
        failed=false;try{await sink.OnMessageAsync(A,B,request,()=>true,default);}catch(StoreException){failed=true;}
        Check(failed&&journal.Recent.All(r=>r.MessageId is null),"BK-02 D-30 unknown block state omits diagnostic UUID conservatively");
        faults.After=null;faults.Read=null;
        Console.WriteLine("PASS BK-02 D-30 blocked/unknown-state failure diagnostics exclude identifiers");
    }
    sealed class ReceiptPeer(Action? afterHello) : HttpMessageHandler
    {
        internal int Receipts, Hellos;
        internal bool StaleFirstPost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            request.Options.Set(ClassroomSender.LocalEndpointOptionKey,B);
            using var input=JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellation));var body=input.RootElement;
            byte[] response;
            if(request.RequestUri!.AbsolutePath.EndsWith("/hello",StringComparison.Ordinal))
            {
                response=ClassroomPacketWriter.HelloOk(body.GetProperty("client_nonce").GetString()!,ClassroomPacketWriter.NewToken(),A,B,["msg.plain","receipt.read"]);
                Hellos++;
                afterHello?.Invoke();
            }
            else if(StaleFirstPost&&Receipts==0)
            {
                Receipts++;
                var (status,errorBody)=ClassroomPacketWriter.Error(ClassroomCodes.StaleSession);
                return new((HttpStatusCode)status){Content=new ByteArrayContent(errorBody)};
            }
            else
            {
                Receipts++;
                response=ClassroomPacketWriter.ReceiptAck("ok",body.GetProperty("message_id").GetString()!,body.GetProperty("session").GetString()!,B,A);
            }
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(response)};
        }
    }
    static async Task D30ReceiptFence()
    {
        using var e=new Env();
        PendingClassroomReceipt Pending()
        {
            var rx=Receive(e.Store,A);
            e.Store.MarkClassroomVisibleRead(rx.Principal,[rx.LocalSeq],new(rx.Principal.Value,true,true,false,true));
            return e.Store.ListPendingClassroomReceipts().Single();
        }
        var pending=Pending();
        using(var peer=new ReceiptPeer(()=>e.Store.SetSourceBlocked(A,true)))
        using(var http=new HttpClient(peer))
        {
            var result=await new ClassroomSender(e.Store,http).SendReceiptAsync(pending,8768,B,()=>true);
            Check(!result.Completed&&result.Status=="stale_policy"&&peer.Receipts==0,"BK-02 D-30 block during hello prevents queued read receipt transmission");
            Check(e.Store.ListPendingClassroomReceipts().Count==0,"BK-02 D-30 block purges the in-flight candidate's queued record");
        }
        e.Store.SetSourceBlocked(A,false);
        Check(e.Store.ListPendingClassroomReceipts().Count==0,"BK-05 D-30 unblock never revives the cancelled read receipt");
        pending=Pending();
        using(var peer=new ReceiptPeer(null))
        using(var http=new HttpClient(peer))
        {
            var result=await new ClassroomSender(e.Store,http).SendReceiptAsync(pending,8768,B,()=>true);
            Check(result.Completed&&peer.Receipts==1,"BK-02 D-30 positive control sends a new unblocked receipt normally");
        }
        Console.WriteLine("PASS BK-02/BK-05 D-30 in-flight receipt fence and unblocked positive control");
    }
    static void LoopbackBlockFixture(ChatStore store,bool blocked)
    {
        // Product contacts deliberately reject loopback addresses. Keep that guard intact:
        // only this isolated TCP fixture maps an explicitly-created private-IP block to loopback.
        // The normal public API and real private addresses are exercised by BK-01..08 above.
        if(blocked)store.SetSourceBlocked(A,true);
        using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Pooling=false}.ToString()))
        {c.Open();using var q=c.CreateCommand();q.CommandText="UPDATE BlockedSource SET ip=@to WHERE ip=@from";q.Parameters.AddWithValue("@from",blocked?A:"127.0.0.1");q.Parameters.AddWithValue("@to",blocked?"127.0.0.1":A);q.ExecuteNonQuery();}
        if(!blocked)store.SetSourceBlocked(A,false);
    }
    static long AttemptTries(ChatStore store,string id)
    {
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Pooling=false}.ToString());c.Open();
        using var q=c.CreateCommand();q.CommandText="SELECT tryCount FROM ClassroomAttempt WHERE messageId=@id";q.Parameters.AddWithValue("@id",id);return Convert.ToInt64(q.ExecuteScalar());
    }
    static string RowCounts(ChatStore store)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Pooling=false}.ToString());c.Open();
        using var names = c.CreateCommand();names.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables=new List<string>();using(var reader=names.ExecuteReader())while(reader.Read())tables.Add(reader.GetString(0));
        return string.Join(";",tables.Select(table=>{using var q=c.CreateCommand();q.CommandText="SELECT count(*) FROM \""+table.Replace("\"","\"\"")+"\"";return table+":"+q.ExecuteScalar();}));
    }
    static async Task D30Transport()
    {
        using var root=new Env();
        await using var receiver=new Node(Path.Combine(root.Root,"receiver"));
        await using var sender=new Node(Path.Combine(root.Root,"sender"));
        const string ip="127.0.0.1",secret="BLOCKED-BODY-SENSITIVE";
        bool localRejected=false;try{receiver.Store.SetSourceBlocked(ip,true);}catch(ArgumentException){localRejected=true;}
        Check(localRejected,"BK-02 D-30 fixture does not loosen product loopback contact guard");
        LoopbackBlockFixture(receiver.Store,true);
        string before=RowCounts(receiver.Store);
        var sink=new ClassroomStoreSink(receiver.Store,receiver.Journal);int notices=0;
        sink.MessageStored+=(_,_,_)=>notices++;sink.ReceiptStored+=(_,_)=>notices++;sink.LegacyMessageStored+=_=>notices++;
        string id=Guid.NewGuid().ToString("D"),session=ClassroomPacketWriter.NewToken();
        var request=new ClassroomRequest(ClassroomRequestKind.Message,MessageId:id,Session:session,Text:secret,Reaction:"wave");
        var ack=await sink.OnMessageAsync(ip,ip,request,()=>true,default);
        Check(ack is{StatusCode:200}&&ack.Body.Span.SequenceEqual(ClassroomPacketWriter.MessageAck("stored",id,session,ip,ip,"wave",secret)),"BK-02 D-30 normal stored ACK with exact content hash/reaction");
        Check((await sink.OnMessageAsync(ip,ip,request,()=>true,default))!.Body.Span.SequenceEqual(ack!.Body.Span),"BK-02 D-30 repeat remains stored without retained dedup id");
        Check((await sink.OnMessageAsync(ip,ip,request,()=>false,default))?.StatusCode!=200,"BK-02 D-30 stopped receiver does not synthesize success");
        var receipt=await sink.OnReceiptAsync(ip,ip,new(ClassroomRequestKind.Receipt,MessageId:id,Session:session),()=>true,default);
        Check(receipt is{StatusCode:200}&&receipt.Body.Span.SequenceEqual(ClassroomPacketWriter.ReceiptAck("discarded",id,session,ip,ip)),"BK-02 D-30 receipt discarded without event");
        var legacy=await sink.OnLegacyDirectAsync(ip,new(secret,"wave"),()=>true,default);
        Check(legacy is{StatusCode:200}&&Encoding.UTF8.GetString(legacy.Body.Span)=="{\"status\":\"delivered\"}","BK-02 D-30 legacy uses frozen success response");
        Check(RowCounts(receiver.Store)==before&&notices==0,"BK-02 D-30 no table changes after text/repeat/reaction/receipt/legacy");
        var p=ClassroomPrincipalKey.ForSourceIp(ip);
        string outgoing=Queue(sender.Store,p,secret,receiver.Listener.Port);
        // Exercise the real sender ACK validator and durable retry loop, not a fake success callback.
        await using(var outbox=new ClassroomOutbox(sender.Store,new(sender.Store),sender.Files,(_,_)=>ip))
        {
            var until=DateTime.UtcNow.AddSeconds(5);
            while(sender.Store.ListOutbox().Single(r=>r.MessageId==outgoing).State!="complete"&&DateTime.UtcNow<until)await Task.Delay(25);
            Check(sender.Store.ListOutbox().Single(r=>r.MessageId==outgoing).State=="complete","BK-02 D-30 A durable queue completes against blocked B over TCP");
            Check(sender.Store.ListClassroomMessages(p).Items.Single().RemoteStoredAt is not null&&sender.Store.ListClassroomMessages(p).Items.Single().RemoteReadAt is null,"BK-02 D-30 A sees stored, never read");
            long tries=AttemptTries(sender.Store,outgoing);await Task.Delay(350);
            Check(AttemptTries(sender.Store,outgoing)==tries&&sender.Store.ListOutbox().Single().State=="complete","BK-02 D-30 ACK stops automatic retries");
        }
        var previous=(ClassroomStored)sender.Store.ReceiveClassroomMessage(ip,new(id,"previously received"),()=>true);p=previous.Principal;
        sender.Store.MarkClassroomVisibleRead(p,[previous.LocalSeq],new(p.Value,true,true,false,true));
        var pending=sender.Store.ListPendingClassroomReceipts().Single();
        var senderTransport=new ClassroomSender(sender.Store);
        var discarded=await senderTransport.SendReceiptAsync(pending,receiver.Listener.Port,ip,()=>true);
        Check(discarded.Completed&&discarded.Status=="discarded","BK-02 D-30 real receipt sender accepts discarded and stops");
        byte[] bytes=Encoding.UTF8.GetBytes(secret);
        await sender.Files.AddDraftAsync(p,new MemoryStream(bytes),"blocked-sensitive.txt");
        var draft=sender.Store.GetClassroomDraftState(p);sender.Store.SaveClassroomDraft(p,"",draft.Epoch,draft.Revision+1);
        var queuedFile=sender.Store.EnqueueAttachment(p,"",draft.Epoch,draft.Revision+1,receiver.Listener.Port);
        await using(var outbox=new ClassroomOutbox(sender.Store,new(sender.Store),sender.Files,(_,_)=>ip))
        {
            var until=DateTime.UtcNow.AddSeconds(5);
            while(sender.Store.ListOutbox().Single(r=>r.MessageId==queuedFile.MessageId).State!="complete"&&DateTime.UtcNow<until)await Task.Delay(25);
            Check(sender.Store.ListOutbox().Single(r=>r.MessageId==queuedFile.MessageId) is{State:"complete",Failures:0},"BK-02 D-30 offered ACK completes A's durable attachment queue without retry");
        }
        var offered=sender.Store.GetAttachment(queuedFile.LocalSeq)!;
        Check(offered.State=="offered"&&offered.CompletedUtc is null&&offered.StateText=="通知已送出 · 等待收件確認","BK-02 D-30 attachment notification sent, never claims complete");
        Check(RowCounts(receiver.Store)==before&&receiver.Notices==0&&receiver.Successes==0,"BK-02 D-30 TCP text/receipt/offer create no content/contact/unread/receipt/attachment records or notifications");
        Check(receiver.Journal.Recent.All(e=>e.MessageId is null),"BK-02 D-30 diagnostics retain no blocked message IDs");
        string export=Path.Combine(root.Root,"diagnostics.zip");Check((await receiver.Journal.ExportAsync(export)).Success,"BK-02 D-30 real diagnostic export succeeds");
        using(var zip=ZipFile.OpenRead(export))
        {
            string content=string.Join("",zip.Entries.Select(entry=>{using var reader=new StreamReader(entry.Open());return reader.ReadToEnd();}));
            Check(new[]{ip,A,B,secret,id,outgoing,offered.MessageId,"blocked-sensitive.txt"}.All(value=>!content.Contains(value,StringComparison.Ordinal)),"BK-02 D-30 exported logs exclude source, IDs, filename and body");
        }
        LoopbackBlockFixture(receiver.Store,false);
        Check(receiver.Store.GetClassroomInboxSnapshot().Entries.Count==0,"BK-05 D-30 unblock does not backfill acknowledged discarded traffic");
        var restored=(ClassroomStored)receiver.Store.ReceiveClassroomMessage(ip,new(id,secret,"wave"),()=>true);
        Check(receiver.Store.ListClassroomMessages(restored.Principal).Items.Single().MessageId==id,"BK-05 D-30 same id explicit resend after unblock is accepted (isolated loopback ingress)");
        Console.WriteLine("PASS BK-02/BK-05 D-30 real sender/TCP, offered-only attachment, discarded receipts, no receiver rows/identifiers");
    }
    static string Forge(Env e,string sql){e.Number(sql);return sql;}
    static async Task Hardening()
    {
        // ---- receipt sender fence: before hello, and before the stale-session retry --------------------------------------------------
        using(var e=new Env())
        {
            PendingClassroomReceipt Pending(string ip){var rx=Receive(e.Store,ip);e.Store.MarkClassroomVisibleRead(rx.Principal,[rx.LocalSeq],new(rx.Principal.Value,true,true,false,true));return e.Store.ListPendingClassroomReceipts().Single();}
            Add(e.Store,A);var pending=Pending(A);
            e.Store.SetSourceBlocked(A,true);   // candidate already listed; block commits before the sender starts
            using(var peer=new ReceiptPeer(null))using(var http=new HttpClient(peer))
            {
                var result=await new ClassroomSender(e.Store,http).SendReceiptAsync(pending,8768,B,()=>true);
                Check(result.Status=="stale_policy"&&peer.Hellos==0&&peer.Receipts==0,"BK-02 D-30 listed receipt blocked before send never even says hello");
            }
            e.Store.SetSourceBlocked(A,false);pending=Pending(A);
            using(var peer=new ReceiptPeer(null){StaleFirstPost=true})using(var http=new HttpClient(peer))
            {
                var result=await new ClassroomSender(e.Store,http).SendReceiptAsync(pending,8768,B,()=>true);
                Check(result.Completed&&peer.Receipts==2&&peer.Hellos==2,"BK-02 D-30 positive control: stale-session retry re-hellos and resends when not blocked");
            }
            e.Store.SetSourceBlocked(A,true);e.Store.SetSourceBlocked(A,false);
            var again=Receive(e.Store,A);e.Store.MarkClassroomVisibleRead(again.Principal,[again.LocalSeq],new(again.Principal.Value,true,true,false,true));pending=e.Store.ListPendingClassroomReceipts().Single();
            int hello=0;
            using(var peer=new ReceiptPeer(()=>{if(++hello==2)e.Store.SetSourceBlocked(A,true);}){StaleFirstPost=true})using(var http=new HttpClient(peer))
            {
                var result=await new ClassroomSender(e.Store,http).SendReceiptAsync(pending,8768,B,()=>true);
                Check(!result.Completed&&result.Status=="stale_policy"&&peer.Receipts==1,"BK-02 D-30 block during the stale-session re-hello stops the retry POST");
            }
            Console.WriteLine("PASS BK-02 D-30 receipt sender re-checks block before hello and before the stale-session retry");
        }
        // ---- unblocked storage errors keep root-cause correlation --------------------------------------------------------------------
        {
            using var root=new Env();await using var journal=new DiagnosticJournal(Path.Combine(root.Root,"diagnostics"),"0.1.0-beta.12");
            var faults=new Faults();using var e=new Env(faults:faults,diagnostics:journal);
            faults.After=op=>{if(op=="classroom-receive")throw new StoreException(StoreErrorKind.DiskFull,"disk");};
            var sink=new ClassroomStoreSink(e.Store,journal);string id=Guid.NewGuid().ToString("D");
            bool failed=false;try{await sink.OnMessageAsync(A,B,new ClassroomRequest(ClassroomRequestKind.Message,MessageId:id,Session:ClassroomPacketWriter.NewToken(),Text:"x"),()=>true,default);}catch(StoreException){failed=true;}
            Check(failed&&journal.Recent.Any(r=>r.MessageId==id),"BK-02 D-30 unblocked source storage failure keeps the message UUID for correlation");
            Console.WriteLine("PASS BK-02 D-30 unblocked storage failure keeps UUID correlation");
        }
        // ---- adapter's own failure records never carry identifiers for a blocked source -------------------------------------------------
        {
            using var root=new Env();await using var journal=new DiagnosticJournal(Path.Combine(root.Root,"diagnostics"),"0.1.0-beta.12");
            var faults=new Faults();using var e=new Env(faults:faults,diagnostics:journal);e.Store.SetSourceBlocked(A,true);
            var adapter=new ClassroomEndpointAdapter(new ClassroomStoreSink(e.Store,journal),capabilities:["msg.plain","receipt.read"],diagnostics:journal);adapter.SetReady(true);
            var ctx=new ClassroomConnectionContext(A,B);
            async Task<string> Hello(){using var d=JsonDocument.Parse((await adapter.HandleAsync(new("classroom.hello",ClassroomPacketWriter.HelloRequest(ClassroomPacketWriter.NewToken())),ctx,()=>true,default)).Body);return d.RootElement.GetProperty("session").GetString()!;}
            faults.After=op=>{if(op is "classroom-receive" or "classroom-apply-receipt")throw new StoreException(StoreErrorKind.DiskFull,"SENSITIVE");};
            string id=Guid.NewGuid().ToString("D");
            var message=await adapter.HandleAsync(new("classroom.message",ClassroomPacketWriter.MessageSend(id,await Hello(),B,"SENSITIVE")),ctx,()=>true,default);
            var receipt=await adapter.HandleAsync(new("classroom.receipt",ClassroomPacketWriter.ReceiptRequest(id,await Hello(),B)),ctx,()=>true,default);
            Check(message.StatusCode>=500&&receipt.StatusCode>=500&&journal.Recent.Count(r=>r.ExceptionType is not null)>=2,"BK-02 D-30 fixture: adapter failure paths ran for blocked source");
            Check(journal.Recent.All(r=>r.MessageId is null),"BK-02 D-30 adapter commit and receipt failure diagnostics carry no blocked message ID");
            Console.WriteLine("PASS BK-02 D-30 adapter-level failure diagnostics exclude identifiers");
        }
        // ---- outbox / attachment write gates (reachable orderings, then forged state to prove the gate itself) ---------------------------
        using(var e=new Env())
        {
            Add(e.Store,A);Add(e.Store,B);var p=e.Store.ResolveClassroomPrincipal(A)!.Value;
            string id=Queue(e.Store,p,"queued before block");
            var file=new AttachmentFile(Guid.NewGuid().ToString("N"),"offer.txt",1,new string('a',64));
            var offer=e.Store.ReceiveAttachmentOffer(A,8768,Guid.NewGuid().ToString("D"),file,"",new string('b',64),()=>true).Record!;
            Check(e.Store.CanAutoReceiveAttachments(p)&&e.Store.AttachmentCurrent(offer),"BK-02 control: unblocked contact may auto-receive and its offer is current");
            e.Store.SetSourceBlocked(A,true);
            Check(!e.Store.CanAutoReceiveAttachments(p),"BK-02 D-30 blocked contact never auto-receives attachments");
            Check(!e.Store.AttachmentCurrent(offer),"BK-02 D-30 blocked peer's attachment is no longer current (chunk/complete gate)");
            var draft=e.Store.GetClassroomDraftState(p);e.Store.SaveClassroomDraft(p,"after block",draft.Epoch,draft.Revision+1);
            Check(e.Store.BeginClassroomSend(p,"after block",draft.Epoch,draft.Revision+1) is InvalidSend,"BK-04 sending to a blocked contact is refused");
            long outboxBefore=e.Number("SELECT count(*) FROM ClassroomOutbox");
            try{e.Store.EnqueueClassroomMessage(p,"after block",draft.Epoch,draft.Revision+1,8768);}catch(Exception){}
            Check(e.Number("SELECT count(*) FROM ClassroomOutbox")==outboxBefore,"BK-04 queueing to a blocked contact creates no outbox row");
            e.Store.EnqueueAttachmentDownload(offer);
            Check(e.Number("SELECT count(*) FROM ClassroomOutbox WHERE messageId LIKE 'download:%'")==0,"BK-02 D-30 blocked offer never queues a download");
            Check(!e.Store.SetAttachmentState(offer,"downloading")&&e.Store.GetAttachment(offer.LocalSeq)!.State=="cancelled","BK-02 D-30 blocked attachment state cannot be advanced");
            Check(!e.Store.AcknowledgeAttachmentOffer(offer,offer.Epoch,0)&&e.Store.GetAttachment(offer.LocalSeq)!.State=="cancelled","BK-02 D-30 blocked attachment offer cannot be re-acknowledged");
            // forged: put the queue row back as if the block had not cancelled it
            Forge(e,"UPDATE ClassroomOutbox SET state='sending' WHERE messageId='"+id+"'");
            Check(!e.Store.OutboxCurrent(id),"BK-04 forged sending row for a blocked target is never current");
            Forge(e,"UPDATE ClassroomOutbox SET state='queued',nextAttemptUtc='2000-01-01T00:00:00Z' WHERE messageId='"+id+"'");
            Check(e.Store.ClaimOutbox(id,true) is null,"BK-04 forged queued row for a blocked target is never claimed");
            Console.WriteLine("PASS BK-02/BK-04 blocked write gates for sends, downloads, and attachment state");
        }
        using(var e=new Env())   // the download-queue claim gate has no other fence (download rows skip the target check)
        {
            Add(e.Store,A);
            var file=new AttachmentFile(Guid.NewGuid().ToString("N"),"offer.txt",1,new string('a',64));
            var offer=e.Store.ReceiveAttachmentOffer(A,8768,Guid.NewGuid().ToString("D"),file,"",new string('b',64),()=>true).Record!;
            e.Store.EnqueueAttachmentDownload(offer);string id="download:"+offer.LocalSeq;
            Check(e.Number("SELECT count(*) FROM ClassroomOutbox WHERE messageId='"+id+"'")==1,"BK-02 control: unblocked offer queues its download");
            e.Store.SetSourceBlocked(A,true);
            Forge(e,"UPDATE ClassroomOutbox SET state='queued',nextAttemptUtc='2000-01-01T00:00:00Z' WHERE messageId='"+id+"'");
            Check(e.Store.ClaimOutbox(id,true) is null,"BK-02 D-30 a download row for a blocked peer is never claimed");
            Console.WriteLine("PASS BK-02 blocked download claim gate");
        }
        // ---- read receipts: read-through, stale queue rows, legacy delivered ----------------------------------------------------------
        using(var e=new Env())
        {
            Add(e.Store,A);var first=Receive(e.Store,A);var second=Receive(e.Store,A);
            e.Store.MarkClassroomVisibleRead(first.Principal,[first.LocalSeq],new(first.Principal.Value,true,true,false,true));
            string principal=first.Principal.Value;Check(e.Store.ListPendingClassroomReceipts().Count==1,"BK-02 control: unblocked read queues a receipt");
            e.Store.SetSourceBlocked(A,true);
            Forge(e,"INSERT INTO ClassroomReceiptOutbox(originalLocalSeq,principal,receiptKind,createdUtc) VALUES("+first.LocalSeq+",'"+principal+"','read','2026-11-01T00:00:00Z')");
            Check(e.Store.ListPendingClassroomReceipts().Count==0,"BK-02 D-30 a stale queued receipt for a blocked peer is never listed");
            var through=e.Store.MarkClassroomReadThrough(second.Principal,null,second.LocalSeq,false,new(principal,true,true,false,true),()=>true);
            Check(through.Marked>=1&&through.Queued==0,"BK-02 D-30 read-through on a blocked conversation never queues a receipt");
            Console.WriteLine("PASS BK-02 blocked receipt queue gates (list and read-through)");
        }
        using(var e=new Env())
        {
            Add(e.Store,A);var p=e.Store.ResolveClassroomPrincipal(A)!.Value;string id=Queue(e.Store,p,"legacy path");
            e.Store.ClaimOutbox(id);var work=e.Store.GetClassroomPreparedSend(id)!;e.Store.RecordClassroomRoute(id,work.TryCount,ClassroomRoute.Legacy,DateTime.UtcNow);
            e.Store.SetSourceBlocked(A,true);
            Forge(e,"UPDATE ClassroomAttempt SET attemptState='sending' WHERE messageId='"+id+"'");
            Check(e.Store.CompleteClassroomLegacyDelivered(id,A,work.TryCount) is Discarded{Reason:"source-not-allowed"},"BK-02 D-30 a legacy 'delivered' answer from a blocked peer is discarded by the block gate");
            Console.WriteLine("PASS BK-02 blocked legacy delivered gate");
        }
        // ---- teacher via edit / csv --------------------------------------------------------------------------------------------------
        using(var e=new Env())
        {
            string id=e.Store.SaveManualRosterEntry(null,new("alias","1",false,A),Token(e.Store)).EntryId!;
            e.Store.SetSourceBlocked(A,true);
            Check(e.Store.SaveManualRosterEntry(id,new("alias","1",true,A),Token(e.Store)) is{Changed:false,Reason:"blocked"},"BK-07 manual edit cannot make a blocked contact a teacher");
            Check(e.Store.SaveManualRosterEntry(id,new("alias","1",false,B),Token(e.Store)) is{Changed:false,Reason:"blocked"}&&e.Store.GetRosterSnapshot().Single().Entry.Endpoint==A,"BK-01 a blocked contact's address cannot be edited away (would orphan the block)");
            var csv=RosterImportPreview.Create(e.Store.GetRosterSnapshot(),RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip,role\n1,alias,"+A+",teacher")));
            Check(e.Store.ApplyRosterPreview(csv).Applied&&e.Store.GetClassroomTeacherIp() is null&&!e.Store.GetRosterSnapshot().Single().Entry.IsTeacher,"BK-07 CSV import cannot make a blocked contact a teacher");
            Console.WriteLine("PASS BK-07 blocked contact cannot gain teacher or move address by edit/import");
        }
    }
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
            Check(response?.StatusCode==200&&notices==0,"D-30 normal stored ACK and no notification");
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
            Add(e.Store,A);var before=Receive(e.Store,A);
            var unread=Receive(e.Store,A);
            e.Store.MarkClassroomVisibleRead(before.Principal,[before.LocalSeq],new(before.Principal.Value,true,true,false,true));
            Check(e.Store.ListPendingClassroomReceipts().Count==1,"BK-02 D-30 pre-block read receipt exists");
            e.Store.SetSourceBlocked(A,true);
            Check(e.Store.ListPendingClassroomReceipts().Count==0,"BK-02 D-30 block purges unsent read receipts");
            Check(e.Store.MarkClassroomVisibleRead(unread.Principal,[unread.LocalSeq],new(unread.Principal.Value,true,true,false,true)) is{Marked:1,Queued:0},"BK-02 D-30 blocked history read never queues a receipt");
            string during=Guid.NewGuid().ToString("D");
            var ack=await new ClassroomStoreSink(e.Store).OnMessageAsync(A,B,new(ClassroomRequestKind.Message,MessageId:during,Session:ClassroomPacketWriter.NewToken(),Text:"body"),()=>true,default);
            Check(ack is{StatusCode:200},"BK-05 D-30 blocked private-IP send gets normal success");
            e.Store.SetSourceBlocked(A,false);
            Check(e.Store.GetClassroomInboxSnapshot().Entries.Single() is{Blocked:false,CanSend:true}&&e.Store.ListClassroomMessages(before.Principal).Items.Count==2,"unblock keeps previous history with no local backfill");
            Check(Receive(e.Store,A,during).Principal==before.Principal&&before.Principal.IsRoster,"BK-05 D-30 same acknowledged id explicit resend restores normal CT contact ownership");
            Console.WriteLine("PASS BK-05 unblock preserves alias/history without backfill");
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
        await Hardening();
        await D30Transport();
        await D30ReceiptFence();
        await D30FailurePrivacy();
        V15FailureRollback();
        await BlockedAuthorRelay();
        Console.WriteLine($"Blocking: {count} assertions passed; WPF/physical acceptance not run.");
    }
}
