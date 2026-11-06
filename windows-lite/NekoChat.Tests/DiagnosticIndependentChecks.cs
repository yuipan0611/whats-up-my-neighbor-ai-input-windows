using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Diagnostics;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>
/// Independent checks for the local diagnostics PR (written from DIAGNOSTICS.md and the behaviour the PR promises, using only public APIs plus the existing internal IStoreFaults seam).
///   REC   a recovery is announced once per failure episode: the "failed" flag is cleared by the recovery, later successes stay silent, and a NEW failure re-arms it
///   FLOW  diagnostics that cannot be written never stop a real send or a real receive over TCP
///   PRIV  privacy with REAL framework exception messages (they contain the peer address, port and local paths), not only with hand-written "SENSITIVE" strings
/// </summary>
internal static class DiagnosticIndependentChecks
{
    private sealed class FinishFault : IStoreFaults
    {
        internal volatile bool Block;
        public void AfterBegin(string op) { if (Block && op == "outbox-finish") throw new IOException("synthetic finalization failure"); }
        public void BeforeCommit(string op) { }
        public void AfterCommit(string op) { }
    }

    /// <summary>Waits up to 20 s; a timeout is reported as a failed check by the caller (never as a crash).</summary>
    private static async Task<bool> WaitFor(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { while (!predicate()) await Task.Delay(20, timeout.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private sealed class Receiver : IAsyncDisposable
    {
        internal ChatStore Store { get; }
        internal ClassroomTcpListener Listener { get; }
        internal Receiver(string root, DiagnosticJournal? journal = null)
        {
            Store = ChatStore.Open(new(Path.Combine(root, "data")) { Diagnostics = journal });
            var endpoint = new ClassroomEndpointAdapter(new ClassroomStoreSink(Store, journal), capabilities: new[] { "msg.plain", "receipt.read" }, diagnostics: journal);
            endpoint.SetReady(true);
            Listener = new(endpoint, new() { Port = 0, BindAddress = IPAddress.Loopback }); Listener.Start();
        }
        public async ValueTask DisposeAsync() { await Listener.DisposeAsync(); Store.Dispose(); }
    }

    private static string ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path); var text = new StringBuilder();
        foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); text.Append(reader.ReadToEnd()); }
        return text.ToString();
    }

    internal static async Task<int> RunAsync()
    {
        int checks = 0; var failures = new List<string>();
        void Check(bool ok, string label, string detail = "") { checks++; if (!ok) failures.Add(label + (detail.Length > 0 ? " — " + detail : "")); }
        string root = Path.Combine(Path.GetTempPath(), "neko-diag-indep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var p = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
        try
        {
            // ---------------------------------------------------------------- REC
            await using (var receiver = new Receiver(Path.Combine(root, "rec-receiver")))
            {
                await using var journal = new DiagnosticJournal(Path.Combine(root, "rec-journal"), "1.0.0");
                var fault = new FinishFault { Block = true };
                using var store = ChatStore.Open(new(Path.Combine(root, "rec-sender")) { Faults = fault });
                string Queue(string text)
                {
                    var draft = store.GetClassroomDraftState(p);
                    store.SaveClassroomDraft(p, text, draft.Epoch, draft.Revision + 1);
                    return store.EnqueueClassroomMessage(p, text, draft.Epoch, draft.Revision + 1, receiver.Listener.Port);
                }
                long Recoveries() => journal.Recent.Count(e => e.Stage == DiagnosticStage.OutboxRecovery && e.Outcome == DiagnosticOutcome.Recovered);
                string first = Queue("REC first (finalization will fail)");
                await using var queue = new ClassroomOutbox(store, new(store), null, (_, _) => "127.0.0.1", journal);
                int failed = 0, recovered = 0; queue.Failed += _ => Interlocked.Increment(ref failed); queue.Recovered += () => Interlocked.Increment(ref recovered);
                Check(await WaitFor(() => Volatile.Read(ref failed) > 0), "WAIT first failure");
                Check(Volatile.Read(ref recovered) == 0 && Recoveries() == 0, "REC while finalization keeps failing nothing is announced as recovered");
                fault.Block = false;
                Check(await WaitFor(() => store.ListOutbox().Single(x => x.MessageId == first).State == "complete"), "WAIT first message completes after repair");
                Check(await WaitFor(() => Volatile.Read(ref recovered) > 0), "WAIT first recovery announced");
                await Task.Delay(300);
                int afterFirst = Volatile.Read(ref recovered); long journalAfterFirst = Recoveries();
                Check(afterFirst == 1 && journalAfterFirst >= 1, "REC the first recovery is announced exactly once to the UI", $"events={afterFirst} journal={journalAfterFirst}");

                // ordinary successes after the recovery stay silent: the failure flag was cleared by the recovery
                string second = Queue("REC second (healthy)"), third = Queue("REC third (healthy)");
                Check(await WaitFor(() => store.ListOutbox().Where(x => x.MessageId == second || x.MessageId == third).All(x => x.State == "complete")), "WAIT two healthy messages complete");
                await Task.Delay(300);
                Check(Volatile.Read(ref recovered) == afterFirst, "REC later healthy sends do not announce a recovery again (the flag was cleared)", $"events={Volatile.Read(ref recovered)} expected={afterFirst}");
                Check(Recoveries() == journalAfterFirst, "REC the journal records no further Recovered events after the flag was cleared", $"journal={Recoveries()} expected={journalAfterFirst}");

                // a NEW failure episode arms it again, and its recovery is announced again
                fault.Block = true; int failedBefore = Volatile.Read(ref failed);
                string fourth = Queue("REC fourth (finalization fails again)");
                Check(await WaitFor(() => Volatile.Read(ref failed) > failedBefore), "WAIT second failure");
                fault.Block = false;
                Check(await WaitFor(() => store.ListOutbox().Single(x => x.MessageId == fourth).State == "complete"), "WAIT fourth completes after repair");
                Check(await WaitFor(() => Volatile.Read(ref recovered) > afterFirst), "WAIT second recovery announced");
                await Task.Delay(300);
                Check(Volatile.Read(ref recovered) == afterFirst + 1, "REC a new failure episode is recovered and announced exactly once more", $"events={Volatile.Read(ref recovered)} expected={afterFirst + 1}");
                Check(receiver.Store.ListClassroomMessages(p).Items.Count == 4, "REC every queued message reached the receiver exactly once", "stored=" + receiver.Store.ListClassroomMessages(p).Items.Count);
            }

            // ---------------------------------------------------------------- FLOW
            string blockedDir = Path.Combine(root, "flow-blocked-journal"); File.WriteAllText(blockedDir, "a file where the journal directory should be");
            await using (var senderJournal = new DiagnosticJournal(blockedDir, "1.0.0"))
            await using (var receiverJournal = new DiagnosticJournal(blockedDir + "-receiver", "1.0.0"))
            {
                File.WriteAllText(blockedDir + "-receiver", "also blocked");
                Check(await WaitFor(() => senderJournal.WriteFailures > 0 && receiverJournal.WriteFailures > 0), "WAIT both journals report that they cannot write");
                Check(!senderJournal.PersistentAvailable && !receiverJournal.PersistentAvailable, "FLOW both journals are in memory-only mode before the flows start");
                await using var receiver = new Receiver(Path.Combine(root, "flow-receiver"), receiverJournal);
                using var senderStore = ChatStore.Open(new(Path.Combine(root, "flow-sender")) { Diagnostics = senderJournal });
                var sender = new ClassroomSender(senderStore, diagnostics: senderJournal);
                for (int i = 1; i <= 3; i++)
                try
                {
                    string text = "FLOW message " + i;
                    var draft = senderStore.GetClassroomDraftState(p);
                    senderStore.SaveClassroomDraft(p, text, draft.Epoch, draft.Revision + 1);
                    var begin = (AttemptStarted)senderStore.BeginClassroomSend(p, text, draft.Epoch, draft.Revision + 1);
                    var prepared = senderStore.GetClassroomPreparedSend(begin.MessageId)!;
                    var result = await sender.SendPreparedAsync(prepared, receiver.Listener.Port, "127.0.0.1", () => true);
                    Check(result.Success && result.Completion is Confirmed, $"FLOW message {i}: the send is confirmed although neither journal can write", $"success={result.Success} code={result.Code}");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { Check(false, $"FLOW message {i}: no exception comes out of the send path because of a diagnostics failure", e.GetType().Name + ": " + e.Message); }
                var stored = receiver.Store.ListClassroomMessages(p).Items;
                Check(stored.Count == 3 && stored.Select(m => m.RawText).SequenceEqual(new[] { "FLOW message 1", "FLOW message 2", "FLOW message 3" }), "FLOW the receiver stored all three messages exactly once and in order", "stored=" + stored.Count);
                Check(senderJournal.Recent.Count(e => e.Stage == DiagnosticStage.SendConfirm && e.Outcome == DiagnosticOutcome.Succeeded) == 3, "FLOW the sender's memory journal still traces the three confirmations");
                var export = await senderJournal.ExportAsync(Path.Combine(root, "flow-sender.zip"));
                Check(export.Success && File.Exists(Path.Combine(root, "flow-sender.zip")), "FLOW an unwritable journal still exports its in-memory events");
                Check(File.ReadAllText(blockedDir) == "a file where the journal directory should be", "FLOW the blocking file was not touched");
            }

            // ---------------------------------------------------------------- PRIV (real framework messages)
            int freePort; { var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); freePort = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            Exception http, socket, io = new IOException("could not open '" + Path.Combine(root, "private", "alice.txt") + "'");
            try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; await client.GetAsync($"http://127.0.0.1:{freePort}/"); throw new UnreachableException(); }
            catch (HttpRequestException e) { http = e; }
            try { using var s = new Socket(SocketType.Stream, ProtocolType.Tcp); await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, freePort)); throw new UnreachableException(); }
            catch (SocketException e) { socket = e; }
            string realMessages = http.Message + " | " + socket.Message + " | " + io.Message;
            Check(realMessages.Contains("127.0.0.1") && realMessages.Contains(freePort.ToString()) && realMessages.Contains(root), "PRIV fixture: the real exception messages really contain the address, the port and the local path", realMessages);
            await using (var journal = new DiagnosticJournal(Path.Combine(root, "priv-journal"), "1.0.0"))
            {
                journal.Record(DiagnosticStage.SendHello, DiagnosticOutcome.Failed, error: http);
                journal.Record(DiagnosticStage.SendTransmit, DiagnosticOutcome.Failed, error: socket);
                journal.Record(DiagnosticStage.StorageWrite, DiagnosticOutcome.Failed, error: io);
                journal.Record(DiagnosticStage.UiAction, DiagnosticOutcome.Failed, messageId: "127.0.0.1:" + freePort + " alice.txt");
                var export = await journal.ExportAsync(Path.Combine(root, "priv.zip"));
                string content = export.Success ? ReadZip(Path.Combine(root, "priv.zip")) : "";
                Check(export.Success && content.Length > 0, "PRIV the journal exports the real-exception events");
                foreach (string secret in new[] { "127.0.0.1", freePort.ToString(), root, "alice.txt", http.Message, socket.Message, io.Message })
                    Check(!content.Contains(secret, StringComparison.Ordinal), "PRIV export excludes a real exception message fragment (" + (secret.Length > 24 ? secret[..24] + "…" : secret) + ")");
                Check(content.Contains("NetworkUnavailable") && content.Contains("StorageIo"), "PRIV the fixed classification still survives");
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        foreach (string f in failures) Console.Error.WriteLine("FAIL: " + f);
        Console.WriteLine($"Diagnostics independent checks: {checks - failures.Count}/{checks} passed, {failures.Count} failed.");
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} of {checks} diagnostics independent checks failed.");
        return checks;
    }
}
