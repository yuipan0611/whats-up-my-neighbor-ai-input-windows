using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core.Attachments;
using NekoChat.Core.Diagnostics;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class OutboxChecks
{
    private sealed class Node : IAsyncDisposable
    {
        internal ChatStore Store { get; }
        internal AttachmentService Files { get; }
        internal ClassroomTcpListener Listener { get; }
        internal Node(string root, int port = 0, bool dropAck = false)
        {
            Store = ChatStore.Open(new(Path.Combine(root, "data")));
            Files = new(Store, Path.Combine(root, "files"), 1);
            var endpoint = new AttachmentEndpoint(new ClassroomEndpointAdapter(new ClassroomStoreSink(Store), capabilities: ["msg.plain", "receipt.read", AttachmentLimits.Capability]), Files);
            IClassroomRequestEndpoint active = dropAck ? new LostAck(endpoint) : endpoint;
            Listener = new(active, new() { BindAddress = IPAddress.Loopback, Port = port });
            Listener.Start(); Files.Port = Listener.Port;
        }
        public async ValueTask DisposeAsync() { await Listener.DisposeAsync(); await Files.DisposeAsync(); Store.Dispose(); }
    }
    private sealed class LostAck(IClassroomRequestEndpoint inner) : IClassroomRequestEndpoint
    {
        private bool _dropped;
        public void SetReady(bool ready) => inner.SetReady(ready);
        public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header) => inner.ValidateHead(header);
        public ClassroomTransportResponse Error(string code) => inner.Error(code);
        public async Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame, ClassroomConnectionContext connection, Func<bool> current, CancellationToken cancellation)
        {
            var result = await inner.HandleAsync(frame, connection, current, cancellation);
            if (frame.Route == "classroom.message" && !_dropped) { _dropped = true; return inner.Error("storage_unavailable"); }
            return result;
        }
    }
    private sealed class PostEnqueueReadFault : IStoreFaults
    {
        internal bool Armed;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { }
        public void AfterCommit(string op) { if (op == "outbox-enqueue") Armed = true; }
        public void AfterRead(string op) { if (Armed) throw new IOException("synthetic read failure after enqueue commit"); }
    }
    private sealed class FinishFault : IStoreFaults
    {
        internal volatile bool Block;
        public void AfterBegin(string op) { if (Block && op == "outbox-finish") throw new IOException("synthetic finalization failure"); }
        public void BeforeCommit(string op) { }
        public void AfterCommit(string op) { }
    }
    private static async Task Wait(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("Outbox: " + label); }
        string root = Path.Combine(Path.GetTempPath(), "neko-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var p = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
        string Queue(ChatStore store, string text, int port)
        {
            var draft = store.GetClassroomDraftState(p);
            Check(store.SaveClassroomDraft(p, text, draft.Epoch, draft.Revision + 1).Saved, "save current draft");
            return store.EnqueueClassroomMessage(p, text, draft.Epoch, draft.Revision + 1, port);
        }
        try
        {
            var readFault = new PostEnqueueReadFault();
            using (var store = ChatStore.Open(new(Path.Combine(root, "enqueue-receipt")) { Faults = readFault }))
            {
                store.SaveClassroomDraft(p, "durable receipt", 0, 1);
                var receipt = store.EnqueueClassroomMessageWithDraft(p, "durable receipt", 0, 1, 8768);
                Check(readFault.Armed && receipt.Draft is { RawText: null, Epoch: 1, Revision: 1 }, "enqueue returns committed handoff even when all post-commit reads would fail");
                readFault.Armed = false;
                Check(store.ListOutbox().Single().MessageId == receipt.MessageId, "receipt names the sole durable queued message");
            }
            // New migration never adopts historic failed/cancelled content for automatic sending.
            string upgrade = Path.Combine(root, "upgrade");
            using (var old = ChatStore.Open(new(upgrade) { Migrations = Migration.Default.Take(12).ToArray() }))
            {
                old.SaveClassroomDraft(p, "舊的失敗", 0, 1);
                var begun = (AttemptStarted)old.BeginClassroomSend(p, "舊的失敗", 0, 1);
                old.CompleteClassroomAttempt(begun.MessageId, new FailedOutcome("offline"), null, begun.TryCount);
            }
            using (var store = ChatStore.Open(new(upgrade)))
            {
                Check(store.ListOutbox().Count == 0 && store.ListClassroomPendingMessages(p).Count == 1, "v12 failure remains manual after v13 upgrade");
                var now = DateTime.UtcNow;
                string a = Queue(store, "A", 8768), b = Queue(store, "B", 8768);
                Check(a != b && store.ListOutbox().Count == 2 && store.GetClassroomDraftState(p).RawText is null, "each Send owns a durable independent item and releases composer");
                var newer = store.GetClassroomDraftState(p);
                store.SaveClassroomDraft(p, "不可覆蓋的新草稿", newer.Epoch, newer.Revision + 1);
                var claimed = store.ClaimOutbox(a)!;
                Check(claimed is not null && store.ClaimOutbox(b) is null, "one active send per recipient");
                var active = store.GetClassroomPreparedSend(a)!;
                store.CompleteClassroomAttempt(a, new FailedOutcome("network_error"), null, active.TryCount);
                store.FinishOutbox(a, false, true, "network_error");
                Check(store.ClaimOutbox(a) is null, "transient failure respects backoff");
                store.WakeOutbox();
                Check(store.ClaimOutbox(a) is not null, "network wake retries immediately");
                Check(store.GetClassroomDraftState(p).RawText == "不可覆蓋的新草稿", "background retries never bind or overwrite a new draft");
                store.CancelOutbox(a);
                Check(store.ClaimOutbox(a, true) is null, "cancelled item does not automatically resume");
                store.CancelClassroomAttempt(a);
                Check(store.DeleteClassroomPendingMessage(p, a, store.ListClassroomPendingMessages(p).Single(x => x.Attempt.MessageId == a).Attempt.TryCount).Deleted, "delete cancelled item");
                Check(store.ListOutbox().All(x => x.MessageId != a) && !store.RequeueClassroomAttempt(p, a, 8768), "deleted item cannot be retried");
                Check(store.CompleteClassroomAttempt(a, new RemoteStoredOutcome(now, ChatStore.ClassroomFingerprint("A", null)), "127.0.0.1") is Discarded, "late ACK cannot revive deleted queue item");
            }
            using (var store = ChatStore.Open(new(upgrade)))
                Check(store.ListOutbox().Single().State == "queued" && store.GetClassroomDraftState(p).RawText == "不可覆蓋的新草稿", "queue and new draft survive restart");

            // Real closed TCP endpoint, durable restart, then a receiver comes online on that exact port.
            int port;
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            string senderRoot = Path.Combine(root, "sender"), firstId;
            using (var store = ChatStore.Open(new(Path.Combine(senderRoot, "data"))))
            {
                firstId = Queue(store, "斷線後自動續送", port);
                await using (var outbox = new ClassroomOutbox(store, new(store), null, (_, _) => "127.0.0.1"))
                    await Wait(() => store.ListOutbox().Single().Failures > 0);
                Check(store.ListOutbox().Single().State == "queued", "offline send remains locally queued");
            }
            await using (var receiver = new Node(Path.Combine(root, "receiver"), port, dropAck: true))
            await using (var sender = new Node(senderRoot))
            await using (var outbox = new ClassroomOutbox(sender.Store, new(sender.Store), sender.Files, (_, _) => "127.0.0.1"))
            {
                await Wait(() => sender.Store.ListOutbox().Single(x => x.MessageId == firstId).State == "complete");
                Check(receiver.Store.ListClassroomMessages(p).Items.Count == 1, "real TCP reconnect sends once after restart");
                Check(sender.Store.ListOutbox().Single(x => x.MessageId == firstId).Failures > 0, "lost real stored ACK causes retry without duplicate delivery");
                Check(sender.Store.ListClassroomPendingMessages(p).Count == 0, "success removes failure card source");
                var manualDraft = sender.Store.GetClassroomDraftState(p);
                sender.Store.SaveClassroomDraft(p, "manual historic failure", manualDraft.Epoch, manualDraft.Revision + 1);
                var historic = (AttemptStarted)sender.Store.BeginClassroomSend(p, "manual historic failure", manualDraft.Epoch, manualDraft.Revision + 1);
                sender.Store.CompleteClassroomAttempt(historic.MessageId, new FailedOutcome("offline"), null, historic.TryCount);
                Check(sender.Store.RequeueClassroomAttempt(p, historic.MessageId, port), "explicit retry adopts only the chosen historical item");
                await Wait(() => sender.Store.ListOutbox().Single(x => x.MessageId == historic.MessageId).State == "complete");
                Check(sender.Store.GetClassroomDraftState(p).RawText == "manual historic failure", "adopted retry never clears or advances the live draft behind the editor");
                string same = Queue(sender.Store, "斷線後自動續送", port);
                await Wait(() => sender.Store.ListOutbox().Single(x => x.MessageId == same).State == "complete");
                Check(firstId != same && receiver.Store.ListClassroomMessages(p).Items.Count == 3, "another Send with identical text is a new message");
                var incoming = receiver.Store.ListClassroomMessages(p).Items.First();
                var duplicate = receiver.Store.ReceiveClassroomMessage("127.0.0.1", new(firstId, "斷線後自動續送"), () => true);
                Check(receiver.Store.ListClassroomMessages(p).Items.Count == 3 && duplicate is ClassroomDuplicate, "same wire ID is deduplicated after lost confirmation");

                foreach (string name in new[] { "測試.txt", "測試.png" })
                {
                    string path = Path.Combine(root, name); byte[] bytes = name.EndsWith(".png", StringComparison.Ordinal) ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=") : Encoding.UTF8.GetBytes("binary payload " + name);
                    await File.WriteAllBytesAsync(path, bytes);
                    await sender.Files.AddDraftAsync(p, path);
                    var draft = sender.Store.GetClassroomDraftState(p);
                    sender.Store.SaveClassroomDraft(p, name, draft.Epoch, draft.Revision + 1);
                    var file = sender.Store.EnqueueAttachment(p, name, draft.Epoch, draft.Revision + 1, port);
                    Check(sender.Store.GetAttachmentDraft(p) is null && sender.Files.Vault.Exists(file.File), "attachment handoff retains managed bytes and releases staged draft");
                    await Wait(() => sender.Store.ListOutbox().Single(x => x.MessageId == file.MessageId).State == "complete");
                    var received = receiver.Store.ListAttachments().Single(x => x.MessageId == file.MessageId);
                    receiver.Store.EnqueueAttachmentDownload(received);
                    await using var downloadQueue = new ClassroomOutbox(receiver.Store, new(receiver.Store), receiver.Files, (_, _) => "127.0.0.1");
                    await Wait(() => receiver.Store.GetAttachment(received.LocalSeq)?.State == "complete");
                    Check((await File.ReadAllBytesAsync(receiver.Files.Vault.PathFor(received.File.BlobId))).SequenceEqual(bytes), "automatic attachment transfer preserves exact bytes");
                    await Wait(() => sender.Store.GetAttachment(file.LocalSeq)?.State == "complete");
                    Check(sender.Store.ListClassroomMessages(p).Items.Count(x => x.MessageId == file.MessageId) == 1, "notification and file completion stay one message");
                }
                string cancelled = Queue(sender.Store, "取消不得送達", port);
                outbox.Cancel(cancelled);
                await Task.Delay(400);
                Check(receiver.Store.ListClassroomMessages(p).Items.All(x => x.MessageId != cancelled), "cancel before dispatch stops delivery");
            }
            foreach (string extension in new[] { ".png", ".txt" })
            {
                var hold = new TcpListener(IPAddress.Loopback, 0); hold.Start(); int offlinePort = ((IPEndPoint)hold.LocalEndpoint).Port; hold.Stop();
                await using var sender = new Node(Path.Combine(root, "offline-file-" + extension[1..]));
                string path = Path.Combine(root, "offline" + extension); byte[] content = extension == ".png" ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=") : Encoding.UTF8.GetBytes("persistent bytes " + extension);
                await File.WriteAllBytesAsync(path, content); await sender.Files.AddDraftAsync(p, path);
                var state = sender.Store.GetClassroomDraftState(p);
                sender.Store.SaveClassroomDraft(p, "caption", state.Epoch, state.Revision + 1);
                var attachment = sender.Store.EnqueueAttachment(p, "caption", state.Epoch, state.Revision + 1, offlinePort);
                await using (var failedQueue = new ClassroomOutbox(sender.Store, new(sender.Store), sender.Files, (_, _) => "127.0.0.1"))
                    await Wait(() => sender.Store.ListOutbox().Single().Failures > 0);
                state = sender.Store.GetClassroomDraftState(p); sender.Store.SaveClassroomDraft(p, "next edit", state.Epoch, state.Revision + 1);
                await using var receiver = new Node(Path.Combine(root, "offline-receiver-" + extension[1..]), offlinePort);
                await using var resumed = new ClassroomOutbox(sender.Store, new(sender.Store), sender.Files, (_, _) => "127.0.0.1");
                await Wait(() => sender.Store.ListOutbox().Single().State == "complete");
                Check(sender.Store.GetClassroomDraftState(p).RawText == "next edit", "attachment reconnect preserves next edit: " + extension);
                var remote = receiver.Store.ListAttachments().Single();
                receiver.Store.EnqueueAttachmentDownload(remote);
                await using var receiverQueue = new ClassroomOutbox(receiver.Store, new(receiver.Store), receiver.Files, (_, _) => "127.0.0.1");
                await Wait(() => receiver.Store.GetAttachment(remote.LocalSeq)?.State == "complete");
                Check((await File.ReadAllBytesAsync(receiver.Files.Vault.PathFor(remote.File.BlobId))).SequenceEqual(content), "offline attachment resumes with original bytes: " + extension);
            }
            await using (var receiver = new Node(Path.Combine(root, "fault-receiver")))
            {
                await using var journal = new DiagnosticJournal(Path.Combine(root, "outbox-diagnostics"), "test");
                var fault = new FinishFault { Block = true };
                using var store = ChatStore.Open(new(Path.Combine(root, "fault-sender")) { Faults = fault });
                string id = Queue(store, "收尾儲存失敗", receiver.Listener.Port);
                await using var queue = new ClassroomOutbox(store, new(store), null, (_, _) => "127.0.0.1", journal);
                int failures = 0, recovered = 0; queue.Failed += _ => Interlocked.Increment(ref failures); queue.Recovered += () => Interlocked.Increment(ref recovered);
                await Wait(() => Volatile.Read(ref failures) > 0);
                Check(store.ListOutbox().Single().State == "sending", "failed finalization retains durable work instead of pretending success");
                Check(journal.Recent.Any(e => e.Stage == DiagnosticStage.OutboxAttempt && e.Outcome == DiagnosticOutcome.Failed && e.Code == DiagnosticCode.StorageIo && e.MessageId == id && e.ExceptionType is not null), "finalization root cause retains typed failure and original UUID");
                Check(!journal.Recent.Any(e => e.Stage == DiagnosticStage.OutboxRecovery && e.Outcome == DiagnosticOutcome.Recovered), "failed bookkeeping cannot emit recovery");
                fault.Block = false;
                await Wait(() => store.ListOutbox().Single().State == "complete");
                Check(receiver.Store.ListClassroomMessages(p).Items.Count == 1, "storage recovery drains orphaned send without duplicate");
                await Wait(() => Volatile.Read(ref recovered) > 0);
                Check(Volatile.Read(ref recovered) > 0, "successful durable recovery emits explicit Recovered event");
                Check(journal.Recent.Any(e => e.Stage == DiagnosticStage.OutboxRecovery && e.Outcome == DiagnosticOutcome.Recovered), "durable recovery is recorded after repair");
                Check(journal.Recent.Any(e => e.Stage == DiagnosticStage.OutboxRecovery && e.Outcome == DiagnosticOutcome.Recovered && e.MessageId == id), "orphan recovery retains UUID without fabricating a completed Finish call");
                fault.Block = true;
                string cancel = Queue(store, "收尾失敗仍可取消", receiver.Listener.Port);
                int previous = Volatile.Read(ref failures);
                await Wait(() => Volatile.Read(ref failures) > previous);
                queue.Cancel(cancel); fault.Block = false;
                Check(journal.Recent.Any(e => e.Stage == DiagnosticStage.OutboxCancel && e.Outcome == DiagnosticOutcome.Cancelled && e.MessageId == cancel), "cancel is diagnosable by original UUID");
                await Task.Delay(600);
                Check(store.ListOutbox().Single(x => x.MessageId == cancel).State == "cancelled", "orphan recovery never reactivates cancelled work");
            }
            // Deterministic backoff follows 1/2/4/8/16/30 seconds with a fake store clock.
            DateTime clock = DateTime.UtcNow;
            using (var store = ChatStore.Open(new(Path.Combine(root, "backoff")) { UtcNow = () => clock }))
            {
                string id = Queue(store, "backoff", 8768);
                foreach (int seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
                {
                    Check(store.ClaimOutbox(id) is not null, "due attempt is claimable");
                    var prepared = store.GetClassroomPreparedSend(id)!;
                    store.CompleteClassroomAttempt(id, new FailedOutcome("network_error"), null, prepared.TryCount);
                    store.FinishOutbox(id, false, true, "network_error");
                    clock = clock.AddSeconds(seconds).AddMilliseconds(-1);
                    Check(store.ClaimOutbox(id) is null, "retry not early: " + seconds);
                    clock = clock.AddMilliseconds(1);
                }
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Outbox: {checks} durable SQLite and real TCP checks passed.");
    }
}
