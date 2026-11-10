using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NekoChat.Core;
using NekoChat.Core.Attachments;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.ClassroomFiles;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

public static class ClassroomFileChecks
{
    // Explicit test-only protector. Windows production uses CurrentUser DPAPI, never this adapter.
    private sealed class FixtureSecrets:IShareSecrets { public byte[] Protect(byte[] b)=>b.ToArray(); public byte[] Unprotect(byte[] b)=>b.ToArray(); }
    private sealed class Node:IAsyncDisposable
    {
        public ChatStore Store {get;}
        public AttachmentService Attachments {get;}
        public ClassroomFileService Shares {get;}
        public ClassroomTcpListener Listener {get;}
        public List<SharePeer> Peers {get;}=[];
        public Node(string root,int port=0,long capacity=AttachmentLimits.StorageBytes)
        {
            Store=ChatStore.Open(new(Path.Combine(root,"data")));
            Attachments=new(Store,Path.Combine(root,"files"),1,capacity);
            Shares=new(Store,Attachments,new FixtureSecrets(),()=>Peers.ToArray(),()=>"127.0.0.1");
            var text=new ClassroomEndpointAdapter(new ClassroomStoreSink(Store),capabilities:["msg.plain",AttachmentLimits.Capability,ShareProtocol.Capability]);
            Listener=new(new ClassroomFileEndpoint(new AttachmentEndpoint(text,Attachments),Shares),new(){BindAddress=IPAddress.Loopback,Port=port});
            Listener.Start();Attachments.Port=Listener.Port;
        }
        public SharePeer Peer(bool teacher=false)=>new("127.0.0.1",Listener.Port,teacher);
        public async ValueTask DisposeAsync(){await Listener.DisposeAsync();await Shares.DisposeAsync();await Attachments.DisposeAsync();Store.Dispose();}
    }
    private static async Task<int> Post(Node node,string op,ShareRequest payload)
    {
        using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false});using var body=new ByteArrayContent(ShareProtocol.Bytes(payload));body.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        using var result=await http.PostAsync($"http://127.0.0.1:{node.Listener.Port}"+ShareProtocol.Prefix+op,body);return (int)result.StatusCode;
    }
    public static async Task<int> RunAsync()
    {
        int checks=0;void Check(bool ok,string name){checks++;if(!ok)throw new Exception("Classroom files: "+name);}
        string root=Path.Combine(Path.GetTempPath(),"classroom-files-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"中文練習.py");byte[] bytes=Encoding.UTF8.GetBytes("print('你好')\r\n    # indent\n");await File.WriteAllBytesAsync(path,bytes);
            var a=new Node(Path.Combine(root,"teacher"));var b=new Node(Path.Combine(root,"b"));var c=new Node(Path.Combine(root,"c"));
            try
            {
                a.Peers.AddRange([b.Peer(),c.Peer()]);b.Peers.AddRange([a.Peer(true),c.Peer()]);c.Peers.AddRange([a.Peer(true),b.Peer()]);
                var own=await b.Shares.ShareAsync(path,"課堂教材");Check(own.Owned,"uploader owns share");Check(b.Shares.LocalPath(own)!=null,"immutable managed upload");
                await File.WriteAllTextAsync(path,"changed source");Check((await File.ReadAllBytesAsync(b.Shares.LocalPath(own)!)).SequenceEqual(bytes),"source changes cannot change share");
                await a.Shares.SyncOnceAsync();await c.Shares.SyncOnceAsync();
                var teacher=a.Shares.List().Single();var replica=c.Shares.List().Single();
                Check(!teacher.Owned&&!replica.Owned,"replicas cannot withdraw");
                Check((await File.ReadAllBytesAsync(a.Shares.LocalPath(teacher)!)).SequenceEqual(bytes),"teacher received exact bytes over TCP");
                Check((await File.ReadAllBytesAsync(c.Shares.LocalPath(replica)!)).SequenceEqual(bytes),"classmate replica received bytes");
                Check(teacher.File.Sha256==Convert.ToHexStringLower(SHA256.HashData(bytes)),"verified hash");
                Check(a.Store.GetClassroomInboxSnapshot().Entries.Count==0,"public shares do not become private messages");
                try{await c.Shares.WithdrawAsync(own.File.Id);Check(false,"non-owner rejected");}catch(AttachmentException e){Check(e.Code=="not_owner","ownership enforced");}
                using(var attacker=ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    var forged=ShareProtocol.Sign(own.File,"withdraw",attacker);Check(!ShareProtocol.Valid(forged),"forged key cannot withdraw");
                    Check(await Post(c,"push",new(1,"push",Operation:forged))==403,"TCP rejects forged withdrawal");
                }
                Check(await Post(c,"chunk",new(1,"chunk",Id:own.File.Id,Count:1,Token:AttachmentLimits.Token()))==403,"downloads require bound grant");
                Check(await Post(c,"chunk",new(1,"chunk",Id:own.File.Id,Count:AttachmentLimits.ChunkBytes+1))==403,"oversize chunks rejected");
                var original=b.Store.ClassroomFileOperations(0,100).Single().Operation;
                Check(!b.Store.MergeClassroomFile(original),"duplicate add idempotent");
                await a.DisposeAsync();a=null!;
                string second=Path.Combine(root,"offline.pdf");await File.WriteAllBytesAsync(second,bytes.Concat(bytes).ToArray());
                var failover=await c.Shares.ShareAsync(second);
                await b.Shares.SyncOnceAsync();Check(b.Shares.List().Count==2,"upload continues without teacher");
                var page1=b.Shares.List(limit:1);var page2=b.Shares.List(page1[0].Sequence,1);
                Check(page2.Count==1&&page1[0].File.Id!=page2[0].File.Id&&page1[0].Sequence>page2[0].Sequence,"keyset pages use stable local sequence");
                var back=b.Shares.List().Single(f=>f.File.Id==failover.File.Id);
                Check(b.Shares.LocalPath(back)!=null,"download continues without teacher");
                await b.Shares.WithdrawAsync(own.File.Id);await c.Shares.SyncOnceAsync();
                Check(c.Shares.List().All(f=>f.File.Id!=own.File.Id),"withdraw continues without teacher");
                var withdraw=b.Store.ClassroomFileOperations(0,100).Single(e=>e.Operation.Kind=="withdraw").Operation;
                using(var fresh=ChatStore.Open(new(Path.Combine(root,"withdraw-first"))))
                {
                    fresh.MergeClassroomFile(withdraw);fresh.MergeClassroomFile(original);Check(fresh.ListClassroomFiles().Count==0,"withdraw received before add dominates");
                }
                a=new Node(Path.Combine(root,"teacher"));a.Peers.AddRange([b.Peer(),c.Peer()]);
                b.Peers.Clear();b.Peers.AddRange([a.Peer(true),c.Peer()]);c.Peers.Clear();c.Peers.AddRange([a.Peer(true),b.Peer()]);
                Check(a.Shares.List().Count==1,"old offline snapshot preserved before sync");
                await a.Shares.SyncOnceAsync();Check(a.Shares.List().Count==1&&a.Shares.List()[0].File.Id==failover.File.Id,"rejoining teacher merges add and withdraw without resurrection");
                Check(a.Shares.LocalPath(a.Shares.List()[0])!=null,"rejoining teacher replicates new bytes");
                Check(b.Shares.List().Single().Owned==false,"other uploader remains not owned");
                var id=failover.File.Id;var nodeId=c.Shares.NodeId;
                await c.DisposeAsync();c=new Node(Path.Combine(root,"c"));c.Peers.AddRange([a.Peer(true),b.Peer()]);
                a.Peers.Clear();a.Peers.AddRange([b.Peer(),c.Peer()]);b.Peers.Clear();b.Peers.AddRange([a.Peer(true),c.Peer()]);
                Check(c.Shares.NodeId==nodeId,"node identity survives restart");Check(c.Shares.List().Single().Owned,"own protected credential survives restart");
                Check(c.Shares.LocalPath(c.Shares.List().Single())!=null,"completed bytes survive restart and attachment GC");
                await c.Shares.WithdrawAsync(id);Check(c.Shares.List().Count==0,"restart retains withdrawal authority");
                await b.Shares.SyncOnceAsync();await a.Shares.SyncOnceAsync();
                Check(a.Shares.List().Count==0&&b.Shares.List().Count==0,"withdraw gossips through intermediaries");
                Check(!b.Store.MergeClassroomFile(original)&&b.Shares.List().Count==0,"old replay cannot resurrect");
                Check(b.Store.ClassroomFileOperations(0,100).Select(e=>e.Sequence).Distinct().Count()==4,"one log op per add/withdraw");
            }
            finally {if(a is not null)await a.DisposeAsync();await b.DisposeAsync();await c.DisposeAsync();}
            await File.WriteAllBytesAsync(path,bytes);
            await using(var source=new Node(Path.Combine(root,"source")))
            await using(var small=new Node(Path.Combine(root,"small"),capacity:bytes.Length-1))
            {
                source.Peers.Add(small.Peer());small.Peers.Add(source.Peer());var item=await source.Shares.ShareAsync(path);await small.Shares.SyncOnceAsync();
                var fail=small.Shares.List().Single();Check(fail.State=="failed"&&fail.Problem=="storage_full","space reserved and failure persisted");Check(small.Attachments.Vault.UsedBytes==0,"space failure leaves no partial");
                await source.Attachments.ClearCacheAsync();Check(source.Shares.LocalPath(source.Shares.List().Single())!=null,"cache cleanup preserves owned shared files");
            }
            await using(var source=new Node(Path.Combine(root,"corrupt-source")))
            await using(var sink=new Node(Path.Combine(root,"corrupt-sink")))
            {
                source.Peers.Add(sink.Peer());sink.Peers.Add(source.Peer());var item=await source.Shares.ShareAsync(path);
                await File.WriteAllBytesAsync(source.Shares.LocalPath(item)!,Enumerable.Repeat((byte)7,bytes.Length).ToArray());await sink.Shares.SyncOnceAsync();
                var fail=sink.Shares.List().Single();Check(fail.State=="failed"&&fail.Problem=="hash_mismatch","corrupt transfer rejected");Check(sink.Attachments.Vault.UsedBytes==0,"hash failure removes partial");
                await File.WriteAllBytesAsync(source.Shares.LocalPath(item)!,bytes);await sink.Shares.DownloadAsync(item.File.Id);
                Check(sink.Shares.LocalPath(sink.Shares.List().Single())!=null,"manual retry restarts and verifies full content");
                await sink.Attachments.ClearCacheAsync();Check(sink.Shares.LocalPath(sink.Shares.List().Single())==null,"explicit cleanup can remove replica cache without withdrawing");
                Check(sink.Shares.List().Count==1,"cache cleanup preserves shared catalog");
            }
            await using(var source=new Node(Path.Combine(root,"cancel-source")))
            await using(var sink=new Node(Path.Combine(root,"cancel-sink")))
            {
                source.Peers.Add(sink.Peer());sink.Peers.Add(source.Peer());
                string large=Path.Combine(root,"cancel.zip");await File.WriteAllBytesAsync(large,new byte[2*1024*1024]);var item=await source.Shares.ShareAsync(large);
                Action<string,long,long> cancel=(id,done,total)=>sink.Shares.Cancel(id);sink.Shares.Progress+=cancel;
                await sink.Shares.SyncOnceAsync();var stopped=sink.Shares.List().Single();
                Check(stopped.State=="cancelled","cancel after first real TCP chunk persists on item");Check(sink.Attachments.Vault.UsedBytes==0,"cancel deletes part");
                sink.Shares.Progress-=cancel;await sink.Shares.DownloadAsync(item.File.Id);Check(sink.Shares.LocalPath(sink.Shares.List().Single())!=null,"cancelled download manually retries from zero");
                var add=source.Store.ClassroomFileOperations(0,100).Single().Operation;
                using(var fenced=ChatStore.Open(new(Path.Combine(root,"fenced"))))
                {
                    try{fenced.MergeClassroomFile(add,()=>false);Check(false,"stale commit rejected");}catch(AttachmentException){Check(fenced.ClassroomFileOperations().Count==0,"stale completion cannot commit");}
                }
                Check(await Post(sink,"push",new(2,"push",Operation:add))==403,"unsupported shared version rejected");
                source.Peers.Clear();Check(await Post(source,"sync",new(1,"sync"))==403,"off-roster source cannot list shares");
            }
            await using(var source=new Node(Path.Combine(root,"cut-source")))
            await using(var sink=new Node(Path.Combine(root,"cut-sink")))
            {
                source.Peers.Add(sink.Peer());sink.Peers.Add(source.Peer());
                string large=Path.Combine(root,"cut.zip");await File.WriteAllBytesAsync(large,new byte[1024*1024]);var item=await source.Shares.ShareAsync(large);Task? stopped=null;
                sink.Shares.Progress+=(id,done,total)=>{stopped??=source.Listener.DisposeAsync().AsTask();};
                await sink.Shares.SyncOnceAsync();if(stopped is not null)await stopped;
                Check(sink.Shares.List().Single().State=="failed","source disconnect persists failed transfer");Check(sink.Attachments.Vault.UsedBytes==0,"disconnect never retains part as complete");
            }
            await using(var source=new Node(Path.Combine(root,"empty-source")))
            await using(var sink=new Node(Path.Combine(root,"empty-sink")))
            {
                source.Peers.Add(sink.Peer());sink.Peers.Add(source.Peer());string empty=Path.Combine(root,"empty.txt");await File.WriteAllBytesAsync(empty,[]);await source.Shares.ShareAsync(empty);await sink.Shares.SyncOnceAsync();
                Check(new FileInfo(sink.Shares.LocalPath(sink.Shares.List().Single())!).Length==0,"empty file transfers and verifies empty SHA-256");
                await source.Shares.WithdrawAsync(source.Shares.List().Single().File.Id);await source.Attachments.ClearCacheAsync();Check(source.Attachments.Vault.UsedBytes==0,"withdrawn owned content can be explicitly cleaned");
            }
            await using(var source=new Node(Path.Combine(root,"automatic-source")))
            await using(var sink=new Node(Path.Combine(root,"automatic-sink")))
            {
                var reservation=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);reservation.Start();int offlinePort=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
                source.Peers.AddRange([new("127.0.0.1",offlinePort,true),sink.Peer()]);sink.Peers.AddRange([new("127.0.0.1",offlinePort,true),source.Peer()]);
                source.Shares.Start();sink.Shares.Start();var item=await source.Shares.ShareAsync(path);
                async Task Until(Func<bool> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));while(!predicate())await Task.Delay(50,timeout.Token);}
                await Until(()=>sink.Shares.List().Any(f=>sink.Shares.LocalPath(f)!=null));
                Check(sink.Shares.List().Single().File.Id==item.File.Id,"automatic background replication works with teacher unavailable and no manual sync");
                await source.Shares.WithdrawAsync(item.File.Id);await Until(()=>sink.Shares.List().Count==0);
                Check(sink.Shares.List().Count==0,"automatic background withdrawal propagates without teacher");
            }
            using(var key=ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var f=new ShareManifest(ShareProtocol.Id(key),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),"../oops",0,Convert.ToHexStringLower(SHA256.HashData([])),"127.0.0.1","",DateTimeOffset.UtcNow.ToString("O"));
                Check(!ShareProtocol.Valid(ShareProtocol.Sign(f,"add",key)),"signed traversal rejected");
                Check(!ShareProtocol.Valid(ShareProtocol.Sign(f with{Name="a.zip",Size=AttachmentLimits.MaximumFileBytes+1},"add",key)),"signed oversized metadata rejected");
                Check(ShareProtocol.Valid(ShareProtocol.Sign(f with{Name="a.zip",Size=AttachmentLimits.MaximumFileBytes},"add",key)),"25 MiB inclusive upper boundary accepted");
            }
            await using (var node = new Node(Path.Combine(root, "idempotent-dispose")))
            {
                await node.Shares.DisposeAsync();
                await node.Shares.DisposeAsync();
                Check(true, "ClassroomFileService DisposeAsync is idempotent across multiple calls");
            }
            Console.WriteLine($"Classroom files: {checks} real TCP/SQLite, three-node failover, signed withdrawal, restart, quota and hash checks passed.");return checks;
        }
        finally{Directory.Delete(root,true);}
    }
}
