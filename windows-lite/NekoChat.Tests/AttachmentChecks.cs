using NekoChat.Core.Diagnostics;
using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Attachments;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using NekoChat.Core.Roster;

namespace NekoChat.Tests;

public static class AttachmentChecks
{
    public static async Task<bool> RunChildAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--attachment-crash-child") return false;
        string root = args[1], phase = args[2];
        using var store = ChatStore.Open(new(Path.Combine(root,"data")));
        var vault = new AttachmentVault(Path.Combine(root,"files"));
        byte[] bytes = Encoding.UTF8.GetBytes("durable attachment bytes");
        var wire = new AttachmentFile(Guid.NewGuid().ToString("N"),"child.txt",bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var row = store.ReceiveAttachmentOffer("127.0.0.1",8768,Guid.NewGuid().ToString("D"),wire,"",AttachmentLimits.Token(),()=>true).Record!;
        store.SetAttachmentState(row,"receiving");
        await using (var part = vault.Reserve(row.File.BlobId,row.File.Size)) { await part.WriteAsync(bytes); part.Flush(true); }
        if (phase is "renamed" or "committed") await vault.CommitAsync(row.File,default);
        if (phase == "committed") store.SetAttachmentState(row,"complete");
        File.WriteAllText(Path.Combine(root,"ready"),"ready");
        await Task.Delay(Timeout.Infinite); return true;
    }
    private sealed class Node : IAsyncDisposable
    {
        internal ChatStore Store { get; }
        internal AttachmentService Files { get; }
        internal ClassroomTcpListener Listener { get; }
        internal int Notices;
        internal Node(string root, long capacity = AttachmentLimits.StorageBytes, bool support = true, DiagnosticJournal? diagnostics = null)
        {
            Store = ChatStore.Open(new(Path.Combine(root, "data")) { Diagnostics = diagnostics });
            Files = new(Store, Path.Combine(root, "files"), 1, capacity, diagnostics);
            Files.MessageStored += (_, _) => Interlocked.Increment(ref Notices);
            var text = new ClassroomEndpointAdapter(new ClassroomStoreSink(Store), capabilities: support ? ["msg.plain", "receipt.read", AttachmentLimits.Capability] : ["msg.plain", "receipt.read"]);
            Listener = new(new AttachmentEndpoint(text, Files), new() { BindAddress = IPAddress.Loopback, Port = 0 });
            Listener.Start(); Files.Port = Listener.Port;
        }
        public async ValueTask DisposeAsync() { await Listener.DisposeAsync(); await Files.DisposeAsync(); Store.Dispose(); }
        internal void KnownLoopbackFixture()
        {
            // Explicit synthetic fixture: UI correctly disallows configuring loopback as a classroom seat.
            var result = Store.SaveManualRosterEntry(null, new("測試同學", "1", false, "192.168.1.42"), RosterImportPreview.ComputeSnapshotToken(Store.GetRosterSnapshot()));
            if (!result.Changed) throw new Exception("roster fixture failed");
            // Pooled fixture handles survive Dispose and prevent deleting the database on Windows.
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE RosterEntry SET endpoint='127.0.0.1' WHERE id=@id"; cmd.Parameters.AddWithValue("@id", result.EntryId); cmd.ExecuteNonQuery();
        }
    }
    private static async Task Wait(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private static async Task<AttachmentRecord> Send(Node sender, Node receiver, string path, string caption = "")
    {
        var p = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
        await sender.Files.AddDraftAsync(p, path);
        var state = sender.Store.GetClassroomDraftState(p);
        var saved = sender.Store.SaveClassroomDraft(p, caption, state.Epoch, state.Revision + 1);
        if (!saved.Saved) throw new Exception("caption save failed");
        return await sender.Files.SendAsync(p, caption, state.Epoch, state.Revision + 1, receiver.Files.Port);
    }
    private static async Task<int> Post(Node node, string operation, object payload)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload)); content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync($"http://127.0.0.1:{node.Files.Port}" + AttachmentEndpoint.Prefix + operation, content);
        return (int)response.StatusCode;
    }
    public static async Task<int> RunAsync()
    {
        int checks = 0; void Check(bool value, string name) { checks++; if (!value) throw new Exception("Attachment check failed: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "attachments-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Check(AttachmentLimits.Automatic(true, 5 * 1024 * 1024), "5MiB inclusive auto threshold");
            Check(!AttachmentLimits.Automatic(true, 5 * 1024 * 1024 + 1), "large waits for download");
            Check(!AttachmentLimits.Automatic(false, 1), "off-roster never auto downloads");
            foreach (string bad in new[] { "../x", "..", "a\\b", "x:y", "x\n.txt", "x.", "x ", "a/b", "<x>" }) Check(!AttachmentLimits.ValidName(bad), "reject name " + JsonSerializer.Serialize(bad));
            Check(AttachmentLimits.ValidName("課堂練習.py"), "Chinese filename");
            var vault = new AttachmentVault(Path.Combine(root, "vault"), 1024);
            byte[] small = Encoding.UTF8.GetBytes("print('你好')\r\n    pass\n");
            var staged = await vault.StageAsync(new MemoryStream(small), "課堂.py", small.Length);
            Check((await File.ReadAllBytesAsync(vault.PathFor(staged.BlobId))).SequenceEqual(small), "staging real bytes");
            Check(staged.Sha256 == Convert.ToHexStringLower(SHA256.HashData(small)), "staging hash");
            try { using var _ = vault.Reserve(Guid.NewGuid().ToString("N"), 1024); Check(false, "quota must reject"); } catch (AttachmentException e) { Check(e.Code == "storage_full", "quota reserves before transfer"); }
            try { using var _ = vault.Reserve(Guid.NewGuid().ToString("N"), AttachmentLimits.MaximumFileBytes + 1); Check(false, "oversize must reject"); } catch (AttachmentException e) { Check(e.Code == "too_large", "25MiB cap"); }
            try { vault.PathFor("../outside"); Check(false, "path must reject"); } catch (AttachmentException) { Check(true, "generated blob paths only"); }
            try { await vault.StageAsync(new MemoryStream(small), "x.py", small.Length - 1); Check(false, "growth must reject"); } catch (AttachmentException e) { Check(e.Code == "source_changed", "file growth rejected"); }
            try { await vault.StageAsync(new MemoryStream(small), "x.py", small.Length + 1); Check(false, "shrink must reject"); } catch (AttachmentException e) { Check(e.Code == "source_changed", "file shrink rejected"); }
            var corrupt = new AttachmentFile(Guid.NewGuid().ToString("N"), "x", small.Length, new string('0', 64));
            await using (var file = vault.Reserve(corrupt.BlobId, corrupt.Size)) { await file.WriteAsync(small); }
            try { await vault.CommitAsync(corrupt, default); Check(false, "hash must reject"); } catch (AttachmentException e) { Check(e.Code == "hash_mismatch", "bad hash never committed"); }
            Check(!File.Exists(vault.PathFor(corrupt.BlobId)), "no complete corrupt file"); vault.Delete(corrupt.BlobId);
            // v10 database upgrade must preserve text and create exactly one pre-upgrade backup.
            string oldDir = Path.Combine(root, "old"); var p = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
            using (var old = ChatStore.Open(new(oldDir) { Migrations = Migration.Default.Take(10).ToArray() }))
                old.ReceiveClassroomMessage("127.0.0.1", new(Guid.NewGuid().ToString("D"), "舊紀錄"), () => true);
            using (var upgraded = ChatStore.Open(new(oldDir)))
            { Check(upgraded.ListClassroomMessages(p).Items.Single().RawText == "舊紀錄", "migration keeps text"); Check(upgraded.LatestVersion == 13 && upgraded.ListBackups().Count != 0, "additive migration and backup"); }
            string source = Path.Combine(root, "課堂.py"); await File.WriteAllBytesAsync(source, small);
            await using (var sender = new Node(Path.Combine(root, "a")))
            await using (var receiver = new Node(Path.Combine(root, "b")))
            {
                var outgoing = await Send(sender, receiver, source, "中文\r\n  縮排");
                var incoming = receiver.Store.ListAttachments().Single();
                Check(incoming.State == "offered" && !receiver.Files.Vault.Exists(incoming.File), "off-roster metadata only until click");
                Check(sender.Store.GetAttachment(outgoing.LocalSeq)!.State == "offered", "offer ACK is not delivery");
                await File.WriteAllTextAsync(source, "原始檔已改");
                await receiver.Files.DownloadAsync(incoming.LocalSeq);
                Check((await File.ReadAllBytesAsync(receiver.Files.Vault.PathFor(incoming.File.BlobId))).SequenceEqual(small), "TCP sends staged snapshot after source edits");
                Check(receiver.Store.GetAttachment(incoming.LocalSeq)!.State == "complete", "receiver verified complete");
                Check(sender.Store.GetAttachment(outgoing.LocalSeq)!.State == "complete", "sender complete only after ACK");
                Check(receiver.Notices == 1 && incoming.Caption == "中文\r\n  縮排", "one notification and exact caption");
                await sender.Files.RetryOfferAsync(outgoing);
                Check(receiver.Store.ListAttachments().Count == 1 && receiver.Notices == 1, "retry deduplicates notification and history");
                receiver.Store.ClearClassroomConversation(p);
                await sender.Files.RetryOfferAsync(outgoing).ContinueWith(t => { _ = t.Exception; });
                Check(receiver.Store.ListAttachments().Count == 0 && receiver.Notices == 1, "clear tombstone prevents late offer resurrection");
            }
            await File.WriteAllBytesAsync(source, small);
            await using (var sender = new Node(Path.Combine(root, "auto-a")))
            await using (var receiver = new Node(Path.Combine(root, "auto-b")))
            {
                receiver.KnownLoopbackFixture(); var outgoing = await Send(sender, receiver, source);
                await Wait(() => sender.Store.GetAttachment(outgoing.LocalSeq)!.State == "complete");
                var incoming = receiver.Store.ListAttachments().Single();
                Check(receiver.Files.Vault.Exists(incoming.File), "known-seat small attachment automatically receives");
                Check(incoming.Caption == "" && receiver.Store.ListClassroomMessages(ClassroomPrincipalKey.ForRosterEntry(incoming.Principal[7..])).Items.Count == 1, "attachment-only message accepted");
            }
            string large = Path.Combine(root, "large.zip"); byte[] big = RandomNumberGenerator.GetBytes(6 * 1024 * 1024); await File.WriteAllBytesAsync(large, big);
            await using (var sender = new Node(Path.Combine(root, "large-a")))
            await using (var receiver = new Node(Path.Combine(root, "large-b")))
            {
                receiver.KnownLoopbackFixture(); var outgoing = await Send(sender, receiver, large); var incoming = receiver.Store.ListAttachments().Single();
                Check(incoming.State == "offered" && !receiver.Files.Vault.Exists(incoming.File), "known large file waits for click");
                long senderProgress = 0; sender.Files.Progress += (_, done, _) => Interlocked.Exchange(ref senderProgress, done);
                int chunks = 0; Action<long,long,long> stop = (seq,_,_) => { if (Interlocked.Increment(ref chunks) == 1) receiver.Files.Cancel(seq); };
                receiver.Files.Progress += stop;
                try { await receiver.Files.DownloadAsync(incoming.LocalSeq); Check(false, "cancel must interrupt"); } catch (OperationCanceledException) { Check(true, "cancel interrupts socket work"); }
                receiver.Files.Progress -= stop;
                Check(receiver.Store.GetAttachment(incoming.LocalSeq)!.State == "cancelled" && !receiver.Files.Vault.Exists(incoming.File), "cancel leaves no completed file");
                Check(sender.Store.GetAttachment(outgoing.LocalSeq)!.State != "complete", "cancel does not claim delivery");
                await receiver.Files.DownloadAsync(incoming.LocalSeq);
                Check((await File.ReadAllBytesAsync(receiver.Files.Vault.PathFor(incoming.File.BlobId))).SequenceEqual(big), "manual retry all multi-chunk bytes");
                Check(sender.Store.GetAttachment(outgoing.LocalSeq)!.State == "complete", "retry completes sender fact");
                Check(Interlocked.Read(ref senderProgress) == big.Length && sender.Store.GetAttachment(outgoing.LocalSeq)!.CompletedUtc is not null, "sender progress completion follows durable confirmation");
            }
            await using (var sender = new Node(Path.Combine(root, "space-a")))
            await using (var receiver = new Node(Path.Combine(root, "space-b"), capacity: 1))
            {
                await Send(sender, receiver, source); var row = receiver.Store.ListAttachments().Single();
                try { await receiver.Files.DownloadAsync(row.LocalSeq); Check(false, "space must reject"); } catch (AttachmentException e) { Check(e.Code == "storage_full", "full receiver rejects before chunk"); }
                Check(receiver.Files.Vault.UsedBytes == 0 && receiver.Store.GetAttachment(row.LocalSeq)!.State == "failed", "full storage retains honest failed card");
            }
            await using (var sender = new Node(Path.Combine(root, "old-a")))
            await using (var receiver = new Node(Path.Combine(root, "old-b"), support: false))
            {
                try { await Send(sender, receiver, source); Check(false, "old version must reject"); } catch (AttachmentException e) { Check(e.Code == "unsupported_version", "old peer capability rejected"); }
                Check(sender.Store.GetAttachmentDraft(p) is not null && receiver.Store.ListAttachments().Count == 0, "unsupported peer preserves draft and sends no tag");
            }
            await using (var journal = new DiagnosticJournal(Path.Combine(root, "secure-logs"), "1.0.0"))
            await using (var sender = new Node(Path.Combine(root, "secure-a"), diagnostics: journal))
            await using (var receiver = new Node(Path.Combine(root, "secure-b"), diagnostics: journal))
            {
                var outgoing = await Send(sender, receiver, source); var incoming = receiver.Store.ListAttachments().Single();
                int denied = await Post(sender, "chunk", new { protocol="nekochat.attachments", version=1, kind="chunk", message_id=outgoing.MessageId, token=new string('0',64), offset=0, count=1 });
                Check(denied == 404, "wrong token cannot read even one byte");
                foreach (var pair in new[] { (-1L,1), (0L,AttachmentLimits.ChunkBytes+1), (0L,0), (outgoing.File.Size,1) })
                    Check(await Post(sender, "chunk", new { protocol="nekochat.attachments", version=1, kind="chunk", message_id=outgoing.MessageId, token=outgoing.Token, offset=pair.Item1, count=pair.Item2 }) != 200, "chunk offset/count rejected");
                Check(await Post(sender,"complete",new { protocol="nekochat.attachments",version=1,kind="complete",message_id=outgoing.MessageId,token=outgoing.Token,size=outgoing.File.Size,sha256=new string('0',64) }) != 200, "wrong hash cannot confirm delivery");
                Check(sender.Store.GetAttachment(outgoing.LocalSeq)!.State == "offered", "bad complete leaves delivery unconfirmed");
                byte[] damaged = Enumerable.Repeat((byte)42, small.Length).ToArray(); await File.WriteAllBytesAsync(sender.Files.Vault.PathFor(outgoing.File.BlobId), damaged);
                try { await receiver.Files.DownloadAsync(incoming.LocalSeq); Check(false,"corrupt TCP transfer must reject"); } catch (AttachmentException error) { Check(error.Code == "hash_mismatch", "corrupted bytes fail final verification"); }
                Check(!receiver.Files.Vault.Exists(incoming.File) && sender.Store.GetAttachment(outgoing.LocalSeq)!.State != "complete", "corruption never creates completion fact");
                Check(journal.Recent.Any(e => e.Stage == DiagnosticStage.Attachment && e.Code == DiagnosticCode.IntegrityMismatch && e.MessageId == incoming.MessageId), "real corrupted TCP bytes leave a classified UUID-linked integrity diagnostic");
            }
            await using (var sender = new Node(Path.Combine(root, "clear-a")))
            await using (var receiver = new Node(Path.Combine(root, "clear-b")))
            {
                await Send(sender,receiver,large); var row=receiver.Store.ListAttachments().Single();
                receiver.Files.Progress += (_,_,_) => receiver.Store.ClearClassroomConversation(p);
                try { await receiver.Files.DownloadAsync(row.LocalSeq); Check(false,"clear must abort"); } catch(AttachmentException error) { Check(error.Code=="cleared","clear invalidates next chunk"); }
                Check(receiver.Store.ListAttachments().Count==0 && !receiver.Files.Vault.Exists(row.File),"late chunks cannot resurrect cleared history or bytes");
            }
            string empty=Path.Combine(root,"空檔.txt"); await File.WriteAllBytesAsync(empty,[]);
            await using(var sender=new Node(Path.Combine(root,"empty-a")))
            await using(var receiver=new Node(Path.Combine(root,"empty-b")))
            {
                var outgoing=await Send(sender,receiver,empty); var row=receiver.Store.ListAttachments().Single(); await receiver.Files.DownloadAsync(row.LocalSeq);
                Check(new FileInfo(receiver.Files.Vault.PathFor(row.File.BlobId)).Length==0 && sender.Store.GetAttachment(outgoing.LocalSeq)!.State=="complete","empty files verify without fake chunk");
                string exported=Path.Combine(root,"另存.txt"); File.Copy(receiver.Files.Vault.PathFor(row.File.BlobId),exported);
                await receiver.Files.AddDraftAsync(p,source); var draft=receiver.Store.GetAttachmentDraft(p)!; await receiver.Files.ClearCacheAsync();
                Check(File.Exists(exported) && receiver.Files.Vault.Exists(draft.File),"cleanup preserves exported files and pending draft");
                Check(receiver.Store.GetAttachment(row.LocalSeq)!.State=="unavailable" && !receiver.Files.Vault.Exists(row.File),"cleanup keeps history with honest unavailable state");
                Check(receiver.Store.GetAttachment(row.LocalSeq)!.CompletedUtc is not null, "cleanup does not erase verified receive fact");
            }
            var exactVault=new AttachmentVault(Path.Combine(root,"exact")); string exactId=Guid.NewGuid().ToString("N");
            using(var reservation=exactVault.Reserve(exactId,AttachmentLimits.MaximumFileBytes)) Check(reservation.Length==AttachmentLimits.MaximumFileBytes,"25MiB exact boundary accepted and reserved"); exactVault.Delete(exactId);
            string restart = Path.Combine(root, "restart"); string blob;
            using (var store = ChatStore.Open(new(Path.Combine(restart, "data"))))
            {
                await using var files = new AttachmentService(store, Path.Combine(restart, "files"), 8768);
                var draft = await files.AddDraftAsync(p, source); blob = draft.File.BlobId;
            }
            using (var store = ChatStore.Open(new(Path.Combine(restart, "data"))))
            {
                await using var files = new AttachmentService(store, Path.Combine(restart, "files"), 8768);
                Check(store.GetAttachmentDraft(p)?.File.BlobId == blob && File.Exists(files.Vault.PathFor(blob)), "draft survives reopen");
                long epoch = store.GetClassroomDraftState(p).Epoch; store.ClearClassroomConversation(p);
                Check(!store.SaveAttachmentDraft(p, new(blob, "x.py", small.Length, Convert.ToHexStringLower(SHA256.HashData(small))), epoch), "stale staged save rejected after clear");
            }
            foreach(string phase in new[]{"part","renamed","committed"})
            {
                string childRoot=Path.Combine(root,"crash-"+phase); Directory.CreateDirectory(childRoot);
                var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
                if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!)=="dotnet") info.ArgumentList.Add(typeof(AttachmentChecks).Assembly.Location);
                info.ArgumentList.Add("--attachment-crash-child"); info.ArgumentList.Add(childRoot); info.ArgumentList.Add(phase);
                using var child=Process.Start(info)!;
                try { await Wait(()=>File.Exists(Path.Combine(childRoot,"ready"))); child.Kill(entireProcessTree:true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { if(!child.HasExited) { child.Kill(entireProcessTree:true); await child.WaitForExitAsync(); } }
                using var recovered=ChatStore.Open(new(Path.Combine(childRoot,"data")));
                await using var files=new AttachmentService(recovered,Path.Combine(childRoot,"files"),8768);
                var row=recovered.ListAttachments().Single();
                Check(row.State==(phase=="committed"?"complete":"failed"),"forced termination "+phase+" restores honest state");
                Check(!File.Exists(files.Vault.PathFor(row.File.BlobId)+".part") && files.Vault.Exists(row.File)==(phase!="part"),"forced termination "+phase+" preserves only atomically renamed bytes");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Attachments: {checks} checks passed (real TCP, SQLite, staged bytes and SHA-256); fixture cleanup completed. Native clipboard, WPF and two Windows devices: NOT RUN."); return checks;
    }
}
