using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NekoChat.Core.Attachments;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Storage;

namespace NekoChat.Core.ClassroomFiles;

/// <summary>Teacher-preferred, multi-writer anti-entropy. Signed immutable operations merge without electing a single writer.
/// A withdraw permanently dominates an add, including when received first. Content availability is independent of the catalog.</summary>
public sealed class ClassroomFileService : IAsyncDisposable
{
    private readonly ChatStore _store;
    private readonly AttachmentService _attachments;
    private readonly IShareSecrets _secrets;
    private readonly Func<IReadOnlyList<SharePeer>> _peers;
    private readonly Func<string> _ownIp;
    private readonly HttpClient _http = new(ClassroomSender.CreateDefaultHandler()) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sync = new(1), _mutations = new(1);
    private readonly object _gate = new();
    private readonly Dictionary<string,(CancellationTokenSource Cancel,Task Work)> _downloads = [];
    private readonly ConcurrentDictionary<SharePeer,(string Node,long Pull,long Push)> _cursors = new();
    private readonly ConcurrentDictionary<SharePeer,DateTimeOffset> _online = new();
    private readonly ConcurrentDictionary<(string Id,SharePeer Peer),DateTimeOffset> _replicas = new();
    private readonly Dictionary<string,(string Id,string Ip,DateTimeOffset Expires)> _grants = [];
    private Task? _loop;
    private bool _ready;
    private long _generation;
    public string NodeId { get; }
    public event Action? Changed;
    public event Action<string,long,long>? Progress;
    public ClassroomFileService(ChatStore store, AttachmentService attachments, IShareSecrets secrets, Func<IReadOnlyList<SharePeer>> peers, Func<string> ownIp)
    {
        _store=store; _attachments=attachments; _secrets=secrets; _peers=peers; _ownIp=ownIp;
        NodeId=store.EnsureClassroomFileNode(); store.RecoverClassroomFiles();
    }
    private SharePeer[] Peers() => _peers().Where(p=>!_store.IsSourceBlocked(p.Ip)&&!_store.IsAutomaticContact(p.Ip)&&LanInvitation.PrivateIPv4(p.Ip)&&p.Port is >0 and <=65535).Distinct().OrderByDescending(p=>p.Teacher).ThenBy(p=>p.Ip,StringComparer.Ordinal).Take(64).ToArray();
    private bool Allowed(string ip) => Peers().Any(p=>p.Ip==ip);
    public bool IsTeacherNode { get; set; }
    public string HostStatus => IsTeacherNode ? "本機提供課堂檔案 · 同學協助備援" : Peers().Any(p=>p.Teacher&&Recent(_online.GetValueOrDefault(p))) ? "老師電腦在線" : _online.Any(p=>Recent(p.Value)) ? "同學備援接管中" : "本機模式 · 等待其他節點上線";
    private static bool Recent(DateTimeOffset seen) => DateTimeOffset.UtcNow-seen<TimeSpan.FromSeconds(45);
    private void Notify() { try { Changed?.Invoke(); } catch { } }
    public void SetReady(bool ready) { lock(_gate) { _ready=ready; _generation++; _grants.Clear(); if(!ready) foreach(var d in _downloads.Values) d.Cancel.Cancel(); } }
    public void Start() { lock(_gate) { if(_loop is null) _loop=Task.Run(RunAsync); } }
    private async Task RunAsync()
    {
        try { while(!_lifetime.IsCancellationRequested) { try { await SyncOnceAsync(_lifetime.Token).ConfigureAwait(false); } catch(Exception e) when(e is IOException or OperationCanceledException or HttpRequestException or StoreException) { } await Task.Delay(TimeSpan.FromSeconds(10),_lifetime.Token).ConfigureAwait(false); } }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
    }
    public IReadOnlyList<SharedFile> List(long? before=null,int limit=50) => _store.ListClassroomFiles(before,limit).Select(f=>f with { Replicas=_replicas.Count(p=>p.Key.Id==f.File.Id&&Recent(p.Value)) }).ToArray();
    public string? LocalPath(SharedFile row) => row.BlobId is { } blob && _attachments.Vault.Exists(new(blob,row.File.Name,row.File.Size,row.File.Sha256)) ? _attachments.Vault.PathFor(blob) : null;
    private async Task<T> Mutate<T>(Func<CancellationToken,Task<T>> work,CancellationToken cancellation)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_lifetime.Token);
        await _mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        try{return await work(linked.Token).ConfigureAwait(false);}finally{_mutations.Release();}
    }
    public Task<SharedFile> ShareAsync(string path,string note="",CancellationToken cancellation=default)=>Mutate(ct=>ShareCore(path,note,ct),cancellation);
    private async Task<SharedFile> ShareCore(string path,string note,CancellationToken cancellation)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_lifetime.Token);
        await _attachments.FilesGate.WaitAsync(linked.Token).ConfigureAwait(false);
        AttachmentFile? staged=null;
        try
        {
            if(_attachments.CacheClearing) throw new AttachmentException("busy");
            staged=await _attachments.Vault.StageAsync(path,linked.Token).ConfigureAwait(false);
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest=new ShareManifest(ShareProtocol.Id(key),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),staged.Name,staged.Size,staged.Sha256,_ownIp(),note,DateTimeOffset.UtcNow.ToString("O"));
            var op=ShareProtocol.Sign(manifest,"add",key); byte[] raw=key.ExportPkcs8PrivateKey();
            try { _store.MergeClassroomFile(op,()=>!linked.IsCancellationRequested,_secrets.Protect(raw),staged.BlobId); }
            finally { CryptographicOperations.ZeroMemory(raw); }
            staged=null; Notify(); return _store.GetClassroomFile(manifest.Id)!;
        }
        finally { if(staged is not null)_attachments.Vault.Delete(staged.BlobId); _attachments.FilesGate.Release(); }
    }
    public Task WithdrawAsync(string id,CancellationToken cancellation=default)=>Mutate(async ct=>{await WithdrawCore(id,ct).ConfigureAwait(false);return true;},cancellation);
    private Task WithdrawCore(string id,CancellationToken cancellation) => Task.Run(()=> {
        cancellation.ThrowIfCancellationRequested(); var row=_store.GetClassroomFile(id)??throw new AttachmentException("unavailable");
        byte[] encrypted=_store.ClassroomFileKey(id)??throw new AttachmentException("not_owner"); byte[] raw=_secrets.Unprotect(encrypted);
        try { using var key=ECDsa.Create(); key.ImportPkcs8PrivateKey(raw,out _); _store.MergeClassroomFile(ShareProtocol.Sign(row.File,"withdraw",key),()=>!cancellation.IsCancellationRequested); }
        finally { CryptographicOperations.ZeroMemory(raw); }
        Cancel(id); Notify();
    },cancellation);
    public void Cancel(string id) { lock(_gate) if(_downloads.TryGetValue(id,out var job)) job.Cancel.Cancel(); }
    public Task DownloadAsync(string id,CancellationToken cancellation=default) => DownloadTracked(id,null,cancellation);
    private Task DownloadTracked(string id,SharePeer? preferred,CancellationToken cancellation)
    {
        lock(_gate)
        {
            if(_downloads.TryGetValue(id,out var existing))return existing.Work;
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_lifetime.Token);
            var work=Task.Run(async()=> { try { await DownloadCore(id,preferred,cancel.Token).ConfigureAwait(false); } finally { lock(_gate)_downloads.Remove(id); cancel.Dispose(); } });
            _downloads.Add(id,(cancel,work)); return work;
        }
    }
    private async Task DownloadCore(string id,SharePeer? preferred,CancellationToken token)
    {
        await _attachments.DownloadLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _attachments.FilesGate.WaitAsync(token).ConfigureAwait(false);
            string? blob=null;
            try
            {
                if(_attachments.CacheClearing)throw new AttachmentException("busy");
                var row=_store.GetClassroomFile(id)??throw new AttachmentException("withdrawn"); if(LocalPath(row) is not null)return;
                var peers=Peers(); if(preferred is not null)peers=peers.OrderByDescending(p=>p==preferred).ToArray();
                Exception? last=null;
                foreach(var peer in peers)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var status=await Request<ShareStatus>(peer,new(1,"status",Id:id),token).ConfigureAwait(false); if(!status.Available)continue;
                        blob=Guid.NewGuid().ToString("N"); var file=new AttachmentFile(blob,row.File.Name,row.File.Size,row.File.Sha256);
                        if(!_store.SetClassroomFileLocal(id,null,"receiving"))throw new AttachmentException("withdrawn"); Notify();
                        await using(var output=_attachments.Vault.Reserve(blob,file.Size))
                        {
                            for(long offset=0;offset<file.Size;)
                            {
                                token.ThrowIfCancellationRequested(); if(_store.GetClassroomFile(id) is null)throw new AttachmentException("withdrawn");
                                int count=(int)Math.Min(AttachmentLimits.ChunkBytes,file.Size-offset);
                                var bytes=await Send(peer,new(1,"chunk",Id:id,Offset:offset,Count:count,Token:status.Token),count,true,token).ConfigureAwait(false);
                                if(bytes.Length!=count)throw new AttachmentException("invalid_length"); await output.WriteAsync(bytes,token).ConfigureAwait(false); offset+=count;
                                try { Progress?.Invoke(id,offset,file.Size); } catch { }
                            }
                            await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);
                        }
                        await _attachments.Vault.CommitAsync(file,token).ConfigureAwait(false);
                        if(!_store.SetClassroomFileLocal(id,blob,"available"))throw new AttachmentException("withdrawn");
                        _replicas[(id,peer)]=DateTimeOffset.UtcNow; blob=null; Notify();return;
                    }
                    catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException)
                    {
                        if(blob is not null){_attachments.Vault.Delete(blob);blob=null;} last=e;
                        if(token.IsCancellationRequested||e is AttachmentException {Code:"storage_full" or "withdrawn"})throw;
                    }
                }
                throw last??new AttachmentException("unavailable");
            }
            catch(Exception error)
            {
                if(blob is not null)_attachments.Vault.Delete(blob);
                _store.SetClassroomFileLocal(id,null,error is OperationCanceledException?"cancelled":"failed",error is AttachmentException a?a.Code:error is OperationCanceledException?"cancelled":"unavailable"); Notify();throw;
            }
            finally {_attachments.FilesGate.Release();}
        }
        finally {_attachments.DownloadLane.Release();}
    }
    public async Task SyncOnceAsync(CancellationToken cancellation=default)
    {
        if(!await _sync.WaitAsync(0,cancellation).ConfigureAwait(false))return;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_lifetime.Token);
        try
        {
            await Parallel.ForEachAsync(Peers(),new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=linked.Token},async(peer,token)=> {
                try { await SyncPeer(peer,token).ConfigureAwait(false); }
                catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or StoreException) { }
            }).ConfigureAwait(false);
            Notify();
        }
        finally {_sync.Release();}
    }
    private async Task SyncPeer(SharePeer peer,CancellationToken token)
    {
        var cursor=_cursors.GetValueOrDefault(peer);
        for(int pages=0;pages<8;pages++)
        {
            var page=await Request<ShareSyncPage>(peer,new(1,"sync",After:cursor.Pull,NodeId:cursor.Node),token).ConfigureAwait(false);
            if(!AttachmentLimits.IsId(page.NodeId)||page.Items is null||page.Items.Count>2)throw new AttachmentException("invalid_payload");
            if(page.NodeId!=cursor.Node)cursor=(page.NodeId,0,0);
            long prior=cursor.Pull;
            foreach(var item in page.Items)
            {
                if(item.Sequence<=prior)throw new AttachmentException("invalid_payload");
                _store.MergeClassroomFile(item.Operation);prior=item.Sequence;
                if(item.Operation.Kind=="withdraw")Cancel(item.Operation.File.Id);
            }
            cursor=(cursor.Node,prior,cursor.Push);_cursors[peer]=cursor;_online[peer]=DateTimeOffset.UtcNow;
            if(page.Items.Count<2)break;
        }
        foreach(var entry in _store.ClassroomFileOperations(cursor.Push,16))
        {
            var ack=await Request<SharePushAck>(peer,new(1,"push",Operation:entry.Operation),token).ConfigureAwait(false);
            if(!ack.Accepted)throw new AttachmentException("unavailable"); cursor=(cursor.Node,cursor.Pull,entry.Sequence);_cursors[peer]=cursor;
        }
        // Bound probes per round; rotate through the catalog so large libraries cannot starve newer/older shares.
        var rows=List(limit:1024); int start=rows.Count==0?0:(int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds()/10*16)%rows.Count);
        foreach(var row in rows.Skip(start).Concat(rows.Take(start)).Take(16))
        {
            var status=await Request<ShareStatus>(peer,new(1,"status",Id:row.File.Id),token).ConfigureAwait(false);
            if(status.Available)_replicas[(row.File.Id,peer)]=DateTimeOffset.UtcNow;else _replicas.TryRemove((row.File.Id,peer),out _);
            if(status.Available&&LocalPath(row) is null&&row.State=="available")
                try { await DownloadTracked(row.File.Id,peer,token).ConfigureAwait(false); } catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException) { }
        }
    }
    private sealed record SharePushAck(bool Accepted);
    private async Task<T> Request<T>(SharePeer peer,ShareRequest payload,CancellationToken token) => ShareProtocol.Parse<T>(await Send(peer,payload,8192,false,token).ConfigureAwait(false));
    private async Task<byte[]> Send(SharePeer peer,ShareRequest payload,int maximum,bool binary,CancellationToken token)
    {
        if(!Peers().Contains(peer))throw new AttachmentException("source_not_allowed");
        using var request=new HttpRequestMessage(HttpMethod.Post,new UriBuilder("http",peer.Ip,peer.Port,ShareProtocol.Prefix+payload.Kind).Uri){Content=new ByteArrayContent(ShareProtocol.Bytes(payload))};
        request.Headers.ConnectionClose=true; request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        var bytes=await AttachmentService.ReadBoundedAsync(response.Content,maximum,token).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new AttachmentException("unavailable");
        if(binary&&response.Content.Headers.ContentType?.MediaType!="application/octet-stream")throw new AttachmentException("invalid_payload");return bytes;
    }
    public async Task<ClassroomTransportResponse> HandleAsync(string operation,ReadOnlyMemory<byte> body,string source,Func<bool> current,CancellationToken token)
    {
        try
        {
            long generation;lock(_gate){if(!_ready)throw new AttachmentException("receiver_not_ready");generation=_generation;}
            bool Live(){lock(_gate)return _ready&&_generation==generation&&current()&&!token.IsCancellationRequested;}
            if(!Allowed(source)||!Live())throw new AttachmentException("source_not_allowed");
            var request=ShareProtocol.Parse<ShareRequest>(body); if(request.Version!=1||request.Kind!=operation)throw new AttachmentException("unsupported_version");
            if(operation=="sync")return Json(new ShareSyncPage(NodeId,_store.ClassroomFileOperations(request.NodeId==NodeId?request.After:0)));
            if(operation=="push")
            {
                if(request.Operation is null)throw new AttachmentException("invalid_payload");
                bool changed=_store.MergeClassroomFile(request.Operation,Live);
                if(request.Operation.Kind=="withdraw")Cancel(request.Operation.File.Id); if(changed)Notify(); return Json(new SharePushAck(true));
            }
            if(request.Id is null||!AttachmentLimits.IsHash(request.Id))throw new AttachmentException("invalid_id");
            var row=_store.GetClassroomFile(request.Id); string? path=row is null?null:LocalPath(row);
            if(operation=="status")
            {
                string grant="";
                if(path is not null)lock(_gate)
                {
                    foreach(var expired in _grants.Where(p=>p.Value.Expires<DateTimeOffset.UtcNow).Select(p=>p.Key).ToArray())_grants.Remove(expired);
                    grant=_grants.FirstOrDefault(p=>p.Value.Id==request.Id&&p.Value.Ip==source).Key??"";
                    if(grant.Length==0) { if(_grants.Count>=1024)throw new AttachmentException("busy"); grant=AttachmentLimits.Token(); }
                    _grants[grant]=(request.Id,source,DateTimeOffset.UtcNow.AddMinutes(2));
                }
                return Json(new ShareStatus(path is not null,grant));
            }
            if(operation!="chunk"||row is null||path is null||request.Offset<0||request.Count is <1 or >AttachmentLimits.ChunkBytes||request.Offset>row.File.Size-request.Count)throw new AttachmentException("unavailable");
            lock(_gate)if(request.Token is null||!_grants.TryGetValue(request.Token,out var grant)||grant.Id!=request.Id||grant.Ip!=source||grant.Expires<DateTimeOffset.UtcNow)throw new AttachmentException("invalid_token");
            await _attachments.ServeLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(!Live()||_store.GetClassroomFile(request.Id) is null||_attachments.CacheClearing)throw new AttachmentException("unavailable");
                byte[] bytes=new byte[request.Count];await using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous)){file.Position=request.Offset;await file.ReadExactlyAsync(bytes,token).ConfigureAwait(false);}
                await Task.Delay(30,token).ConfigureAwait(false);
                if(!Live()||_store.GetClassroomFile(request.Id) is null)throw new AttachmentException("withdrawn");return new(200,bytes,true);
            }
            finally{_attachments.ServeLane.Release();}
        }
        catch(Exception e) when(e is IOException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or OverflowException or StoreException)
        {return new(403,ShareProtocol.Bytes(new {Error=e is AttachmentException a?a.Code:"invalid_payload"}));}
    }
    private static ClassroomTransportResponse Json<T>(T value)=>new(200,ShareProtocol.Bytes(value));
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);SetReady(false);if(_loop is not null)await _loop.ConfigureAwait(false);
        Task[] jobs;lock(_gate)jobs=_downloads.Values.Select(j=>j.Work).ToArray();try{await Task.WhenAll(jobs).ConfigureAwait(false);}catch(Exception e)when(e is IOException or OperationCanceledException or HttpRequestException){}
        await _sync.WaitAsync().ConfigureAwait(false);_sync.Release();await _mutations.WaitAsync().ConfigureAwait(false);_mutations.Release();_http.Dispose();_sync.Dispose();_mutations.Dispose();_lifetime.Dispose();
    }
}
