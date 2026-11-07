using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Attachments;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Diagnostics;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class DiagnosticChecks
{
    private sealed class PrivateDetailException(string message) : Exception(message);
    private sealed class AckFaultHandler : DelegatingHandler
    {
        internal string? Fault;
        internal AckFaultHandler() : base(ClassroomSender.CreateDefaultHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Fault == "timeout") { Fault = null; throw new TaskCanceledException("SENSITIVE simulated network timeout"); }
            if (Fault == "unexpected") { Fault = null; throw new InvalidOperationException("SENSITIVE transport adapter bug"); }
            var response = await base.SendAsync(request, cancellation);
            if (request.RequestUri!.AbsolutePath.EndsWith("/message", StringComparison.Ordinal))
            {
                if (Fault == "lost-ack") { Fault = null; response.Dispose(); throw new HttpRequestException("SENSITIVE ACK lost after receiver committed"); }
                if (Fault == "bad-ack") { Fault = null; response.Content.Dispose(); response.Content = new ByteArrayContent("SENSITIVE invalid response"u8.ToArray()); }
            }
            return response;
        }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Exception? WriteError, ReadError;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { if (WriteError is { } error) throw error; }
        public void AfterCommit(string op) { }
        public void AfterRead(string op) { if (ReadError is { } error) throw error; }
    }
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException("Diagnostics #" + checks + ": " + label); }
        string root = Path.Combine(Path.GetTempPath(), "nekochat-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var pair in new[] {
                (new StoreException(StoreErrorKind.StorageBusy, "private"), DiagnosticCode.StorageBusy),
                (new StoreException(StoreErrorKind.DiskFull, "private"), DiagnosticCode.DiskFull),
                (new StoreException(StoreErrorKind.Corrupt, "private"), DiagnosticCode.CorruptData),
                (new StoreException(StoreErrorKind.ReadOnly, "private"), DiagnosticCode.ReadOnly)
            }) Check(DiagnosticFault.FromException(pair.Item1).Code == pair.Item2, "typed SQLite fault classification " + pair.Item2);
            Check(DiagnosticFault.FromException(new OperationCanceledException()).Code == DiagnosticCode.Timeout, "timeout is distinct from explicit cancellation");
            Check(DiagnosticFault.FromException(new OperationCanceledException(), true).Code == DiagnosticCode.Cancelled, "explicit cancellation is informational");
            Check(DiagnosticFault.FromException(new IOException("private network failure"), stage: DiagnosticStage.SendTransmit).Code == DiagnosticCode.NetworkUnavailable &&
                DiagnosticFault.FromException(new IOException("private storage failure"), stage: DiagnosticStage.StorageRead).Code == DiagnosticCode.StorageIo, "I/O classification uses the owning boundary rather than treating network streams as disks");
            Check(DiagnosticFault.FromException(new AttachmentException("hash_mismatch")).Category == DiagnosticCategory.DataIntegrity, "attachment hash mismatch is integrity fault");
            Check(DiagnosticFault.For(DiagnosticCode.CorruptData).StopDataOperations && !DiagnosticFault.For(DiagnosticCode.StorageBusy).StopDataOperations, "corruption and temporary contention are distinct");
            Check(DiagnosticFault.FromReason("private-token-and-ip").Code == DiagnosticCode.Unexpected, "unknown remote reason maps to a fixed code");

            Check(new[] { DiagnosticCode.OperationBusy, DiagnosticCode.UiNotReady, DiagnosticCode.DraftNotSaved }.All(code => DiagnosticFault.For(code).Category == DiagnosticCategory.State), "send admission guards are fixed state categories rather than network failures");
            Check(DiagnosticFault.FromReason("stale-revision").Code == DiagnosticCode.StaleView && DiagnosticFault.FromReason("stale-epoch").Code == DiagnosticCode.StaleView, "strict draft rejection maps to bounded state metadata");
            Check(DiagnosticFault.FromReason("draft-too-large").Code == DiagnosticCode.InvalidPayload && DiagnosticFault.FromReason("not-found").Code == DiagnosticCode.StaleView, "non-exception draft rejections retain fixed actionable categories");
            await ExportPreservesHistoryAsync(root, Check);
            string folder = Path.Combine(root, "normal");
            await using (var journal = new DiagnosticJournal(folder, "0.1.0-beta.10"))
            {
                Exception sensitive;
                try { ThrowSensitive(); throw new UnreachableException(); }
                catch (Exception error) { sensitive = error; }
                string id = Guid.NewGuid().ToString("D");
                journal.Record(DiagnosticStage.UiAction, DiagnosticOutcome.Failed, error: sensitive, messageId: "SENSITIVE-user-input");
                journal.Record(DiagnosticStage.UiAction, DiagnosticOutcome.Failed,
                    error: new PrivateDetailException("SENSITIVE 10.20.30.40 Bearer alice.txt"));
                Check(journal.Recent.Last().ExceptionType == "OtherException", "unknown custom exception exports fixed OtherException rather than its private message or type");
                journal.Record(DiagnosticStage.SendConfirm, DiagnosticOutcome.Unconfirmed, DiagnosticCode.Unconfirmed, messageId: id, attempt: 2);
                journal.Record(DiagnosticStage.SendAdmission, DiagnosticOutcome.Deferred, DiagnosticCode.UiNotReady);
                for (int i = 0; i < 20; i++) journal.Record(DiagnosticStage.UiRead, DiagnosticOutcome.Deferred);
                var export = await journal.ExportAsync(Path.Combine(root, "normal.zip"));
                Check(export.Success && journal.PersistentAvailable, "local journal writes and exports actual ZIP");
                Check(journal.SuppressedEvents > 0, "repeated idle/state events are coalesced");
                string content = ReadZip(Path.Combine(root, "normal.zip"));
                foreach (string secret in new[] { "SENSITIVE", "10.20.30.40", "Bearer ", "SELECT ", "alice.txt", root })
                    Check(!content.Contains(secret, StringComparison.Ordinal), "diagnostic export excludes sensitive text " + secret.Length);
                Check(content.Contains(id) && content.Contains("Unconfirmed") && content.Contains("SendConfirm"), "UUID, attempt and typed stage survive export");
                Check(content.Contains("SendAdmission") && content.Contains("UiNotReady"), "pre-enqueue guard classification survives export without input or recipient data");
                Check(content.Contains(nameof(ThrowSensitive)), "safe method frames identify unexpected bug without message or source paths");
                using var zip = ZipFile.OpenRead(Path.Combine(root, "normal.zip"));
                Check(zip.Entries.All(entry => entry.Name == "recent.jsonl" || entry.Name == "summary.json" || entry.Name.StartsWith("diagnostics-", StringComparison.Ordinal)), "export includes no database, settings, identity or attachment files");
                Check(!(await journal.ExportAsync(Path.Combine(folder, "unsafe.zip"))).Success, "export cannot overwrite its own journal directory");
            }

            string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "must-survive");
            await using (var journal = new DiagnosticJournal(blocked, "invalid SENSITIVE version"))
            {
                journal.Record(DiagnosticStage.SendTransmit, DiagnosticOutcome.Failed, DiagnosticCode.NetworkUnavailable);
                Check((await journal.ExportAsync(Path.Combine(root, "fallback.zip"))).Success, "blocked log location still exports bounded memory events");
                Check(journal.WriteFailures > 0 && !journal.PersistentAvailable && journal.Recent.Any(e => e.Code == DiagnosticCode.NetworkUnavailable), "logger failure remains visible in counters without losing recent fault");
                Check(File.ReadAllText(blocked) == "must-survive" && !ReadZip(Path.Combine(root, "fallback.zip")).Contains("SENSITIVE"), "blocked external file preserved and invalid version excluded");
            }

            string rotation = Path.Combine(root, "rotation");
            await using (var journal = new DiagnosticJournal(rotation, "1.0.0", new() { FileBytes = 16384, FileCount = 2, QueueCapacity = 4096 }))
                for (int i = 0; i < 180; i++) journal.Record(DiagnosticStage.SendPrepare, DiagnosticOutcome.Started, messageId: Guid.NewGuid().ToString("D"));
            var files = Directory.GetFiles(rotation, "*.jsonl");
            Check(files.Length == 2 && files.All(file => new FileInfo(file).Length <= 16384), "rotation enforces file count and byte bounds");
            int retained = 0;
            foreach (string file in files) foreach (string line in File.ReadLines(file))
            { using var json = JsonDocument.Parse(line); if (json.RootElement.GetProperty("Sequence").GetInt64() <= 0) throw new InvalidDataException("Retained diagnostic sequence is invalid."); retained++; }
            Check(retained > 0, "every retained log line parses as structured JSON with a valid sequence");
            await using (var journal = new DiagnosticJournal(Path.Combine(root, "burst"), "1.0.0", new() { QueueCapacity = 1, RecentCapacity = 8 }))
            {
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++) journal.Record(DiagnosticStage.SendPrepare, DiagnosticOutcome.Started, messageId: Guid.NewGuid().ToString("D"));
                Check(timer.Elapsed < TimeSpan.FromSeconds(5) && journal.DroppedEvents > 0 && journal.Recent.Length <= 8, "saturated log queue drops metadata instead of blocking caller or growing memory");
            }
            string owned = Path.Combine(root, "owned");
            await using (var owner = new DiagnosticJournal(owned, "1.0.0"))
            {
                Check((await owner.ExportAsync(Path.Combine(root, "owner.zip"))).Success, "first journal owns writer");
                await using var second = new DiagnosticJournal(owned, "1.0.0");
                Check((await second.ExportAsync(Path.Combine(root, "second.zip"))).Success && second.WriteFailures > 0, "second writer degrades to memory without rotating active logs");
                Check((await owner.ExportAsync(Path.Combine(root, "still-owned.zip"))).Success, "owner remains usable after competing writer");
            }
            await StorageAndWorkerAsync(Path.Combine(root, "storage"), Check);
            await TcpFaultsAsync(Path.Combine(root, "tcp"), Check);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Diagnostics: {checks} classification, privacy, bounded journal/export, real SQLite/worker and TCP failure/recovery checks passed.");
        return checks;
    }
    private static void ThrowSensitive() => throw new InvalidOperationException("SENSITIVE Bearer token 10.20.30.40 alice.txt SELECT rawText FROM Message", new IOException("SENSITIVE C:\\Users\\private"));
    private static async Task ExportPreservesHistoryAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "read-only-export");
        // Fill every retained generation using real journals. Starting another writer would delete generation 3.
        for (int generation = 0; generation < 4; generation++)
        {
            await using var journal = new DiagnosticJournal(directory, "1.0.0");
            journal.Record(DiagnosticStage.UiAction, DiagnosticOutcome.Failed, messageId: Guid.NewGuid().ToString("D"));
        }
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, NekoChat.TestFixtures.TestFixtureFiles.ReadAllBytes);
        check(before.Count == 5 && before.ContainsKey("diagnostics-3.jsonl"), "export fixture retains four complete generations and writer lock");
        for (int pass = 0; pass < 2; pass++)
        {
            await using var journal = DiagnosticJournal.OpenReadOnly(directory, "1.0.0");
            journal.Record(DiagnosticStage.Journal, DiagnosticOutcome.Succeeded);
            string destination = Path.Combine(root, "read-only-" + pass + ".zip");
            check((await journal.ExportAsync(destination)).Success, "read-only export succeeds repeatedly");
            using var zip = ZipFile.OpenRead(destination);
            foreach (var pair in before.Where(pair => pair.Key!.EndsWith(".jsonl", StringComparison.Ordinal)))
            {
                using var entry = zip.GetEntry(pair.Key!)!.Open(); using var bytes = new MemoryStream(); entry.CopyTo(bytes);
                check(bytes.ToArray().SequenceEqual(pair.Value), "read-only archive includes unchanged retained generation " + pair.Key);
            }
        }
        var after = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, NekoChat.TestFixtures.TestFixtureFiles.ReadAllBytes);
        check(after.Count == before.Count && before.All(pair => after.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes)), "read-only export neither rotates, deletes nor writes diagnostic files");
        string missing = Path.Combine(root, "missing-read-only");
        await using var absent = DiagnosticJournal.OpenReadOnly(missing, "1.0.0");
        check((await absent.ExportAsync(Path.Combine(root, "missing.zip"))).Success && !Directory.Exists(missing), "read-only export does not create a missing source directory");
    }
    private static string ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path); var text = new StringBuilder();
        foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); text.Append(reader.ReadToEnd()); }
        return text.ToString();
    }
    private static async Task StorageAndWorkerAsync(string root, Action<bool, string> check)
    {
        await using var journal = new DiagnosticJournal(Path.Combine(root, "logs"), "1.0.0");
        var faults = new Faults();
        using var store = ChatStore.Open(new(Path.Combine(root, "data")) { Diagnostics = journal, Faults = faults });
        var principal = ClassroomPrincipalKey.ForSourceIp("127.0.0.1"); string id = Guid.NewGuid().ToString("D");
        faults.WriteError = new StoreException(StoreErrorKind.DiskFull, "SENSITIVE");
        bool failed = false;
        try { store.ReceiveClassroomMessage("127.0.0.1", new(id, "SENSITIVE preserved text"), () => true); }
        catch (StoreException error) when (error.Kind == StoreErrorKind.DiskFull) { failed = true; }
        check(failed && store.ListClassroomMessages(principal).Items.Count == 0, "failed pre-commit write rolls back without fake storage success");
        faults.WriteError = null;
        check(store.ReceiveClassroomMessage("127.0.0.1", new(id, "SENSITIVE preserved text"), () => true) is ClassroomStored, "same operation succeeds after disk fault repair");
        check(journal.Recent.Any(e => e.Stage == DiagnosticStage.StorageWrite && e.Code == DiagnosticCode.DiskFull), "write failure recorded with exact classification");
        await using var worker = new ChatStoreWorker(store, capacity: 1, diagnostics: journal);
        faults.ReadError = new IOException("SENSITIVE read fault");
        bool readFailed = false;
        try { await worker.Run(s => s.GetClassroomInboxSnapshot()); } catch (IOException) { readFailed = true; }
        faults.ReadError = null;
        check(readFailed && (await worker.Run(s => s.GetClassroomInboxSnapshot())).Entries.Count == 1, "worker survives failed read and handles next real query");
        check(journal.Recent.Any(e => e.Stage == DiagnosticStage.StorageRead && e.Code == DiagnosticCode.StorageIo), "read failure recorded before boundary handles it");
        using var gate = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = worker.Run(_ => { entered.TrySetResult(); if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return true; });
        try
        {
            await entered.Task; var queued = worker.Run(s => s.GetClassroomInboxSnapshot());
            bool full = false; try { await worker.Run(s => s.GetClassroomInboxSnapshot()); } catch (StorageWorkQueueFullException) { full = true; }
            check(full && journal.Recent.Any(e => e.Code == DiagnosticCode.QueueFull), "full storage queue rejects quickly with typed capacity fault");
            gate.Set(); await busy; await queued;
        }
        finally { gate.Set(); await busy; }
        check((await worker.Run(s => s.GetClassroomInboxSnapshot())).Entries.Count == 1, "storage queue recovers after saturation");
        await journal.ExportAsync(Path.Combine(root, "storage.zip"));
        check(!ReadZip(Path.Combine(root, "storage.zip")).Contains("SENSITIVE"), "real storage exceptions do not leak messages into diagnostics");
    }
    private static async Task TcpFaultsAsync(string root, Action<bool, string> check)
    {
        await using var journal = new DiagnosticJournal(Path.Combine(root, "logs"), "1.0.0");
        var faults = new Faults();
        using var receiver = ChatStore.Open(new(Path.Combine(root, "receiver")) { Diagnostics = journal, Faults = faults });
        using var senderStore = ChatStore.Open(new(Path.Combine(root, "sender")) { Diagnostics = journal });
        var principal = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
        var sink = new ClassroomStoreSink(receiver, journal);
        sink.MessageStored += (_, _, _) => throw new InvalidOperationException("SENSITIVE notification bug");
        var endpoint = new ClassroomEndpointAdapter(sink, capabilities: new[] { "msg.plain", "receipt.read" }, diagnostics: journal);
        endpoint.SetReady(true);
        await using var listener = new ClassroomTcpListener(endpoint, new() { Port = 0, BindAddress = IPAddress.Loopback }); listener.Start();
        using var handler = new AckFaultHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var sender = new ClassroomSender(senderStore, http, diagnostics: journal);
        ClassroomPreparedSend Prepare(string text)
        {
            var draft = senderStore.GetClassroomDraftState(principal);
            senderStore.SaveClassroomDraft(principal, text, draft.Epoch, draft.Revision + 1);
            var begin = (AttemptStarted)senderStore.BeginClassroomSend(principal, text, draft.Epoch, draft.Revision + 1);
            return senderStore.GetClassroomPreparedSend(begin.MessageId)!;
        }
        faults.WriteError = new StoreException(StoreErrorKind.DiskFull, "SENSITIVE receiver disk full");
        var first = Prepare("SENSITIVE first text");
        var failed = await sender.SendPreparedAsync(first, listener.Port, "127.0.0.1", () => true);
        check(!failed.Success && receiver.ListClassroomMessages(principal).Items.Count == 0, "receiver write failure over real TCP never sends successful ACK");
        check(senderStore.GetClassroomDraftState(principal).RawText == first.RawText, "failed transfer retains sender draft");
        check(journal.Recent.Any(e => e.Stage == DiagnosticStage.ReceiveCommit && e.Code == DiagnosticCode.DiskFull && e.MessageId == first.MessageId), "receiver records root storage cause and matching UUID");
        faults.WriteError = null;
        var next = Prepare("SENSITIVE second text");
        var success = await sender.SendPreparedAsync(next, listener.Port, "127.0.0.1", () => true);
        check(success.Success && receiver.ListClassroomMessages(principal).Items.Single().RawText == next.RawText, "subsequent message succeeds without restarting either store or listener");
        check(journal.Recent.Any(e => e.Stage == DiagnosticStage.ReceiveNotify && e.Code == DiagnosticCode.NotificationFailed && e.MessageId == next.MessageId), "post-commit notification bug recorded without undoing durable ACK");
        check(journal.Recent.Any(e => e.Stage == DiagnosticStage.SendConfirm && e.Outcome == DiagnosticOutcome.Succeeded && e.MessageId == next.MessageId), "sender confirmation matches receiver's trace UUID");
        foreach (string fault in new[] { "lost-ack", "bad-ack" })
        {
            handler.Fault = fault;
            var pending = Prepare("SENSITIVE " + fault);
            var uncertain = await sender.SendPreparedAsync(pending, listener.Port, "127.0.0.1", () => true);
            check(!uncertain.Success && uncertain.Completion is StateUpdated { NewState: "unconfirmed" }, fault + " remains unconfirmed without fake success");
            check(receiver.ListClassroomMessages(principal).Items.Count(message => message.MessageId == pending.MessageId) == 1, fault + " receiver already stored exactly once");
            var draft = senderStore.GetClassroomDraftState(principal);
            var retry = (AttemptStarted)senderStore.BeginClassroomRetry(pending.MessageId, principal, pending.RawText, draft.Epoch, draft.Revision);
            var prepared = senderStore.GetClassroomPreparedSend(retry.MessageId)!;
            var recovered = await sender.SendPreparedAsync(prepared, listener.Port, "127.0.0.1", () => true);
            check(recovered.Success && retry.MessageId == pending.MessageId && retry.TryCount == 2, fault + " manual retry reuses original UUID and confirms duplicate ACK");
            check(receiver.ListClassroomMessages(principal).Items.Count(message => message.MessageId == pending.MessageId) == 1, fault + " retry never duplicates stored content");
            check(journal.Recent.Any(e => e.Stage == DiagnosticStage.SendConfirm && e.MessageId == pending.MessageId && e.Outcome == DiagnosticOutcome.Unconfirmed), fault + " uncertain outcome is traceable");
        }
        handler.Fault = "timeout";
        var timeoutAttempt = Prepare("SENSITIVE timeout");
        check(!(await sender.SendPreparedAsync(timeoutAttempt, listener.Port, "127.0.0.1", () => true)).Success &&
            journal.Recent.Any(e => e.Stage == DiagnosticStage.SendHello && e.Code == DiagnosticCode.Timeout && e.MessageId == timeoutAttempt.MessageId), "real sender boundary distinguishes non-requested timeout");
        handler.Fault = "unexpected";
        var bugAttempt = Prepare("SENSITIVE unexpected");
        await sender.SendPreparedAsync(bugAttempt, listener.Port, "127.0.0.1", () => true);
        check(journal.Recent.Any(e => e.Stage == DiagnosticStage.SendHello && e.Category == DiagnosticCategory.Unexpected && e.MessageId == bugAttempt.MessageId && e.Frames.Length > 0), "unexpected adapter bug retains type/frame instead of being hidden by wire network error");
        var cancelled = Prepare("SENSITIVE cancelled"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        check((await sender.SendPreparedAsync(cancelled, listener.Port, "127.0.0.1", () => true, cancellation.Token)).Code == "cancelled" &&
            journal.Recent.Any(e => e.MessageId == cancelled.MessageId && e.Code == DiagnosticCode.Cancelled && e.Severity == DiagnosticSeverity.Information), "explicit cancellation is traceable without error alarm");
        await journal.ExportAsync(Path.Combine(root, "tcp.zip"));
        check(!ReadZip(Path.Combine(root, "tcp.zip")).Contains("SENSITIVE") && !ReadZip(Path.Combine(root, "tcp.zip")).Contains("127.0.0.1"), "real TCP diagnostics omit message bodies and peer IP");
    }
}
