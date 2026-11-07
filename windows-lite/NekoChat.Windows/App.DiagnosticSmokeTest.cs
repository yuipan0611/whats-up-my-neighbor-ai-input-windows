using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Diagnostics;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    private async Task<int> RunDiagnosticSmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory); UiTestOptions.AssertSafeDataDirectory(output); Directory.CreateDirectory(output);
        string root = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "diagnostics"), "keep-blocked-log-location");
        var settings = Settings.ForUiTest(root) with { ClassRosterInitialized = true, AutoStartOnLogin = false };
        var peerSettings = Settings.ForUiTest(root + "-peer") with { ClassRosterInitialized = true, AutoStartOnLogin = false };
        var principal = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
        var checks = new List<string>(); string? failure = null; object? diagnostic = null;
        ChatRuntime? runtime = null, peer = null; ChatWindow? window = null;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Until(Func<bool> condition, string label, int seconds = 18, bool responsive = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try { while (!condition()) {
                long tick = Stopwatch.GetTimestamp(); await Task.Delay(25, timeout.Token);
                if (responsive && Stopwatch.GetElapsedTime(tick) > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("UI dispatcher did not respond during SQLite contention");
            } }
            catch (OperationCanceledException) { throw new TimeoutException(label); }
            checks.Add(label);
        }
        async Task UntilStored(Func<Task<bool>> condition, string label, int seconds = 20)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try { while (!await condition()) await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(label); }
            checks.Add(label);
        }
        try
        {
            runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            runtime.Start();
            await runtime.Worker.Run(store => store.SaveClassroomDraft(principal, "PRIVATE-DIAG-CHAT fixture draft", 0, 1));
            window = new(settings); await window.AttachRuntimeAsync(runtime, 0); window.SetReceiverState(true, runtime.Listener.Port, null);
            window.Show(); window.Activate(); await window.SmokeOpenEntryAsync(entry => entry.ConversationId == principal.Value);
            await Until(() => window.IsActive && window.Draft.IsEnabled, "native window remains usable with blocked diagnostic disk");
            Check(runtime.Diagnostics.WriteFailures > 0 && !runtime.Diagnostics.PersistentAvailable, "logger degrades to memory without blocking runtime startup");
            window.Draft.Text = "";
            Check(!window.SendButton.IsEnabled, "empty composer keeps actual Send disabled");
            window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(runtime.Diagnostics.Recent.Any(e => e.Stage == DiagnosticStage.SendAdmission && e.Outcome == DiagnosticOutcome.Deferred && e.Code == DiagnosticCode.UiNotReady), "pre-enqueue disabled Send leaves a fixed state diagnostic");
            var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start(); int unavailablePort = ((IPEndPoint)unused.LocalEndpoint).Port; unused.Stop();
            window.SmokeUsePeerPort(unavailablePort);
            const string first = "PRIVATE-DIAG-CHAT first offline message";
            window.Draft.Text = first;
            await Until(() => window.SendButton.IsEnabled, "first send is enabled");
            window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.SmokeSendIdle && window.Draft.Text.Length == 0, "durable enqueue clears composer and releases controls while offline");
            await UntilStored(() => runtime.Worker.Run(store => ((ChatStore)store).ListOutbox().SingleOrDefault()?.Failures > 0), "offline connection failure remains durably queued");
            string firstId = await runtime.Worker.Run(store => ((ChatStore)store).ListOutbox().Single().MessageId);
            Check(runtime.Diagnostics.Recent.Any(e => e.Stage == DiagnosticStage.SendHello && e.Category == DiagnosticCategory.Network), "network exception has classified local trace");
            Check(runtime.Diagnostics.Recent.Any(e => e.Stage == DiagnosticStage.OutboxEnqueue && e.MessageId == firstId), "enqueue diagnostic names committed receipt without a post-commit database read");
            const string next = "PRIVATE-DIAG-CHAT next message succeeds";
            window.Draft.Text = next;
            await Until(() => window.SendButton.IsEnabled, "next draft remains editable while first message is pending");
            peer = await ChatRuntime.OpenAsync(new(peerSettings, SettingsSourceStatus.Loaded), true, unavailablePort, loopbackOnly: true);
            peer.Start(); runtime.Outbox?.Wake();
            await UntilStored(() => peer.Worker.Run(store => store.ListClassroomMessages(principal).Items.Any(row => row.MessageId == firstId && row.RawText == first)), "reconnect delivers original queued UUID to receiver SQLite");
            await UntilStored(() => runtime.Worker.Run(store => ((ChatStore)store).ListOutbox().Single().State == "complete"), "background retry completes original durable item");
            Check(window.Draft.Text == next && window.SendButton.IsEnabled, "background completion preserves next draft and send controls");
            window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.SmokeSendIdle && window.Draft.Text.Length == 0, "second Send enqueues without restart or confirmation blockage");
            await UntilStored(() => peer.Worker.Run(store => store.ListClassroomMessages(principal).Items.Count == 2 && store.ListClassroomMessages(principal).Items.Any(row => row.RawText == next)), "receiver SQLite stores both messages exactly once");

            // Saturate the actual bounded worker, then invoke the actual refresh entrypoint.
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var blocked = runtime.Worker.Run(_ => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return true; });
                var queued = new List<Task<InboxSnapshot>>();
                try
                {
                    await entered.Task;
                    for (int i = 0; i < 64; i++) queued.Add(runtime.Worker.Run(store => store.GetClassroomInboxSnapshot().ToInboxSnapshot()));
                    window.DataChanged();
                    await Until(() => window.StorageStatus.Text.Contains("NCD-QueueFull"), "refresh queue failure is shown as a typed recoverable error");
                    Check(window.SmokeRefreshIdle && runtime.Diagnostics.Recent.Any(e => e.Stage == DiagnosticStage.UiRefresh && e.Code == DiagnosticCode.QueueFull), "failed refresh resets its busy flag and leaves a diagnostic event");
                }
                finally { release.Set(); await blocked; await Task.WhenAll(queued); }
            }
            var extra = ClassroomPrincipalKey.ForSourceIp("127.0.0.2");
            await runtime.Worker.Run(store => store.SaveClassroomDraft(extra, "PRIVATE-DIAG-CHAT recovery row", 0, 1));
            window.DataChanged();
            await Until(() => window.InboxList.Items.Count == 2 && window.SmokeRefreshIdle, "refresh recovers and displays actual new SQLite row");

            Check(!window.StorageStatus.Text.Contains("NCD-QueueFull"), "successful refresh clears its recovered queue error");

            // The visible read, Send flush and autosave can each consume the existing 5-second SQL timeout.
            // Bound their complete queued flow while checking the actual UI Dispatcher every 25ms.
            // Keep the chat foreground so this exercises the competing viewport write too.
            using (var locked = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(runtime.DataDirectory, "nekochat.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                locked.Open(); using var transaction = locked.BeginTransaction(deferred: false);
                const string busyText = "PRIVATE-DIAG-CHAT preserve during SQLite busy";
                window.Draft.Text = busyText;
                await Until(() => window.SendButton.IsEnabled, "storage contention send can start");
                window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.SmokeSendIdle && window.StorageStatus.Text.Contains("NCD-StorageBusy"), "real SQLite busy timeout is handled without crashing or leaving send busy", seconds: 35, responsive: true);
                Check(window.Draft.Text == busyText && runtime.Diagnostics.Recent.Any(e => e.Stage == DiagnosticStage.StorageWrite && e.Code == DiagnosticCode.StorageBusy), "SQLite failure preserves edit and identifies storage stage");
                transaction.Rollback();
            }
            await Until(() => {
                if (!window.SendButton.IsEnabled) return false;
                window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true;
            }, "recovered composer accepts the next Send on the same Dispatcher turn");
            await Until(() => window.SmokeSendIdle && window.Draft.Text.Length == 0, "send succeeds after SQLite lock is released");
            await UntilStored(() => peer.Worker.Run(store => store.ListClassroomMessages(principal).Items.Count == 3), "storage recovery delivers third message without duplicating earlier deliveries");
            await Until(() => !window.StorageStatus.Text.Contains("NCD-StorageBusy"), "successful enqueue clears recovered SQLite busy warning");
            string directoryTarget = Path.Combine(output, "not-a-zip"); Directory.CreateDirectory(directoryTarget);
            Check(!(await window.ExportDiagnosticsAsync(directoryTarget)).Success && window.ExportDiagnosticsButton.IsEnabled, "failed export restores its button");
            string archive = Path.Combine(output, "diagnostics.zip");
            Check((await window.ExportDiagnosticsAsync(archive)).Success && window.ExportDiagnosticsButton.IsEnabled, "memory-only journal exports a real diagnostic ZIP after export failure");
            using (var zip = ZipFile.OpenRead(archive))
            {
                string combined = "";
                foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); combined += reader.ReadToEnd(); }
                Check(!combined.Contains("PRIVATE-DIAG-CHAT") && !combined.Contains("127.0.0.1") && !combined.Contains(root), "native export excludes chat, peer IP and profile path");
                Check(combined.Contains("StorageBusy") && combined.Contains("QueueFull") && combined.Contains("NetworkUnavailable"), "native export retains independent failures for bug diagnosis");
            }
            Check(File.ReadAllText(Path.Combine(root, "diagnostics")) == "keep-blocked-log-location", "logger never deletes the conflicting external file");
            // Exercise the actual no-runtime export entrypoint with every retained generation populated.
            string history = Path.Combine(root, "retained-history");
            for (int generation = 0; generation < 4; generation++)
            {
                await using var journal = new DiagnosticJournal(history, ChatRuntime.DiagnosticVersion);
                journal.Record(DiagnosticStage.UiAction, DiagnosticOutcome.Failed, messageId: Guid.NewGuid().ToString("D"));
            }
            var retained = Directory.GetFiles(history).ToDictionary(path => Path.GetFileName(path)!, NekoChat.TestFixtures.TestFixtureFiles.ReadAllBytes);
            Check(retained.Count == 5 && retained.ContainsKey("diagnostics-3.jsonl"), "native read-only export fixture has all four retained generations");
            window.DetachRuntime(); window.SetDiagnosticsDirectory(history);
            Check((await window.ExportDiagnosticsAsync(Path.Combine(output, "no-runtime-1.zip"))).Success, "actual no-runtime window exports retained diagnostic history");
            Check((await window.ExportDiagnosticsAsync(Path.Combine(output, "no-runtime-2.zip"))).Success, "actual no-runtime window exports repeatedly without starting a writer");
            var afterExport = Directory.GetFiles(history).ToDictionary(path => Path.GetFileName(path)!, NekoChat.TestFixtures.TestFixtureFiles.ReadAllBytes);
            Check(afterExport.Count == retained.Count && retained.All(pair => afterExport.TryGetValue(pair.Key, out var bytes) && bytes.SequenceEqual(pair.Value)), "native export does not rotate, delete or write retained diagnostic files");
            Capture(window, Path.Combine(output, "diagnostics-recovered.png"));
        }
        catch (Exception error)
        {
            failure = error.ToString(); diagnostic = new { view = window?.SmokeReadState, send = window?.SmokeSendState, recent = runtime?.Diagnostics.Recent };
            if (window?.IsVisible == true) Capture(window, Path.Combine(output, "diagnostics-failure.png"));
        }
        finally
        {
            try { if (window is not null) await window.PrepareForShutdownAsync(); window?.DetachRuntime(); window?.Hide(); }
            catch (Exception error) { failure ??= error.ToString(); }
            try { if (runtime is not null) await runtime.DisposeAsync(); if (peer is not null) await peer.DisposeAsync(); }
            catch (Exception error) { failure ??= error.ToString(); }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "diagnostics-result.json"), JsonSerializer.Serialize(new { passed = failure is null, checks, failure, diagnostic, dailyProfileUsed = false, loopbackOnly = true }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
