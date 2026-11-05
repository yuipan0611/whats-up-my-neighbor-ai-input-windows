using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    private async Task<int> RunMessageRecoverySmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory), fixture = Path.Combine(output, "isolated-profile");
        Directory.CreateDirectory(output);
        var checks = new List<string>(); string? failure = null; ChatWindow? window = null;
        ChatRuntime? runtime = null, peer = null;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        GetCursorPos(out var originalCursor); DndAllowedProcesses.Add(Environment.ProcessId);
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Wait(Func<bool> condition) => await UntilAsync(condition, deadline.Token);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task DeleteViaMenu(MessageView row)
        {
            window!.History.ScrollIntoView(row); window.UpdateLayout();
            var card = FindDndVisuals<MessageCard>(window.History).Single(c => ReferenceEquals(c.Message, row));
            var border = FindDndVisuals<Border>(card).First(b => b.ContextMenu?.Items.OfType<MenuItem>().Any(m => Equals(m.Header, "從本機刪除…")) == true);
            var menu = border.ContextMenu!; menu.PlacementTarget = border; menu.IsOpen = true;
            await Wait(() => menu.IsOpen);
            var deletion = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "從本機刪除…"));
            var confirmation = Task.Run(async () => {
                IntPtr dialog;
                while ((dialog = DndFindWindow("#32770", "從本機刪除")) == IntPtr.Zero) await Task.Delay(25, deadline.Token).ConfigureAwait(false);
                DndGetWindowThreadProcessId(dialog, out int pid);
                if (pid != Environment.ProcessId) throw new InvalidOperationException("Delete dialog belongs to another process");
                DndPostMessage(dialog, 0x111, new IntPtr(6), IntPtr.Zero); // IDYES, only our isolated confirmation.
            });
            deletion.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
            await confirmation;
            await Wait(() => !window.History.Rows.Contains(row));
        }
        try
        {
            var settings = Settings.ForUiTest(fixture) with { ClassRosterInitialized = true };
            var peerSettings = Settings.ForUiTest(fixture + "-peer") with { ClassRosterInitialized = true };
            runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            runtime.Start();
            var principal = ClassroomPrincipalKey.ForSourceIp("127.0.0.1");
            await runtime.Worker.Run(s => s.SaveClassroomDraft(principal, "第一則：離線失敗", 0, 1));
            var portReservation = new TcpListener(IPAddress.Loopback, 0); portReservation.Start();
            int closedPort = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
            window = new(settings);
            runtime.DataChanged += () => Dispatcher.BeginInvoke(new Action(() => window?.DataChanged()));
            await window.AttachRuntimeAsync(runtime, closedPort);
            window.SetReceiverState(true, runtime.Listener.Port, null); window.Show(); window.Activate();
            await window.SmokeOpenEntryAsync(e => e.ConversationId == principal.Value);
            await Wait(() => window.SendButton.IsEnabled); Click(window.SendButton);
            await Wait(() => window.SmokeSendIdle && window.SmokeSelectedEntry?.Attempt?.State == "queued");
            var first = window.SmokeSelectedEntry!.Attempt!;
            Check(window.History.Rows.Any(r => r.AttemptId == first.MessageId), "real offline send leaves one queued message");
            Check(window.Draft.Text.Length == 0, "durable enqueue releases composer before delivery");
            window.Draft.Text = "第二則：恢復連線後獨立送達";
            peer = await ChatRuntime.OpenAsync(new(peerSettings, SettingsSourceStatus.Loaded), true, closedPort, loopbackOnly: true);
            peer.Start();
            await Wait(() => window.History.Rows.All(r => r.AttemptId != first.MessageId));
            Check(await peer.Worker.Run(s => s.ListClassroomMessages(principal).Items.Count == 1), "reconnection automatically delivers queued message exactly once");
            Check(window.Draft.Text == "第二則：恢復連線後獨立送達", "background delivery preserves new edit");
            await Wait(() => window.SendButton.IsEnabled); Click(window.SendButton);
            await Wait(() => window.SmokeSendIdle && window.Draft.Text.Length == 0 && window.SmokeSelectedEntry?.Attempt is null);
            Check(await peer.Worker.Run(s => s.ListClassroomMessages(principal).Items.Count == 2), "next Send remains independent");
            Check(window.History.Rows.Count(r => r.RawText == first.RawSnapshot) == 1 && window.History.Rows.All(r => r.AttemptId is null), "success leaves no duplicate or stale failure card");
            Check(window.History.Rows.Last().RawText == "第二則：恢復連線後獨立送達", "history and latest message follow chronological order");
            await window.SmokeOpenEntryAsync(e => e.ConversationId == principal.Value);
            var delivered = window.History.Rows.Single(r => r.RawText == "第二則：恢復連線後獨立送達" && r.AttemptId is null);
            await DeleteViaMenu(delivered);
            await window.SmokeOpenEntryAsync(e => e.ConversationId == principal.Value);
            Check(window.History.Rows.All(r => r.RawText != delivered.RawText) && await peer.Worker.Run(s => s.ListClassroomMessages(principal).Items.Count == 2), "actual message delete menu persists locally and leaves remote history intact");

            var anotherReservation = new TcpListener(IPAddress.Loopback, 0); anotherReservation.Start();
            int offlinePort = ((IPEndPoint)anotherReservation.LocalEndpoint).Port; anotherReservation.Stop();
            window.SmokeUsePeerPort(offlinePort); window.Draft.Text = "第三則：刪掉失敗草稿";
            await Wait(() => window.SendButton.IsEnabled); Click(window.SendButton);
            await Wait(() => window.SmokeSendIdle && window.SmokeSelectedEntry?.Attempt?.State == "queued");
            Click(window.CancelAttemptButton);
            await Wait(() => window.SmokeSelectedEntry?.Attempt?.State == "cancelled");
            Check(window.Draft.Text.Length == 0, "cancel stops queued send without changing composer");
            var third = window.History.Rows.Single(r => r.AttemptId == window.SmokeSelectedEntry!.Attempt!.MessageId);
            await DeleteViaMenu(third);
            Check(window.Draft.Text.Length == 0 && await runtime.Worker.Run(s => s.ListClassroomPendingMessages(principal).Count == 0), "delete queued message removes durable retry ownership");

            var historic = await runtime.Worker.Run(s => {
                var draft = s.GetClassroomDraftState(principal);
                s.SaveClassroomDraft(principal, "舊失敗手動重送", draft.Epoch, draft.Revision + 1);
                var begun = (AttemptStarted)s.BeginClassroomSend(principal, "舊失敗手動重送", draft.Epoch, draft.Revision + 1);
                s.CompleteClassroomAttempt(begun.MessageId, new FailedOutcome("offline"), null, begun.TryCount);
                return begun;
            });
            window.DataChanged();
            await Wait(() => window.History.Rows.Any(row => row.AttemptId == historic.MessageId));
            window.SmokeUsePeerPort(peer.Listener.Port);
            window.Draft.Text = "重送時也不能覆蓋的新草稿";
            var oldFailure = window.History.Rows.Single(row => row.AttemptId == historic.MessageId);
            window.History.ScrollIntoView(oldFailure); window.UpdateLayout();
            var failedCard = FindDndVisuals<MessageCard>(window.History).Single(card => ReferenceEquals(card.Message, oldFailure));
            Click(FindDndVisuals<Button>(failedCard).Single(button => Equals(button.Content, "重試傳送")));
            await Wait(() => window.History.Rows.All(row => row.AttemptId != historic.MessageId));
            Check(window.Draft.Text == "重送時也不能覆蓋的新草稿", "native retry button preserves another edit without clearing composer");
            Check(await peer.Worker.Run(s => s.ListClassroomMessages(principal).Items.Count == 3 && s.ListClassroomMessages(principal).Items.Any(m => m.MessageId == historic.MessageId)), "native manual retry reuses original ID and stores once");

            await runtime.Worker.Run(s => s.SaveManualRosterEntry(null, new RosterEdit("原名", "01", false, "10.0.0.21"), RosterImportPreview.ComputeSnapshotToken(s.GetRosterSnapshot())));
            await window.SmokeOpenEntryAsync(e => e.Endpoint == "10.0.0.21");
            window.UpdateLayout();
            var item = (ListBoxItem)window.InboxList.ItemContainerGenerator.ContainerFromItem(window.InboxList.Items.OfType<InboxRowView>().Single(r => r.Entry.Endpoint == "10.0.0.21"));
            var point = item.PointToScreen(new Point(Math.Min(50, item.ActualWidth / 2), item.ActualHeight / 2));
            DndSetForegroundWindow(new WindowInteropHelper(window).Handle);
            if (DndProcessAt(point) != Environment.ProcessId) throw new InvalidOperationException("Nickname right-click target is not our isolated window");
            SetCursorPos((int)point.X, (int)point.Y); MouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero); MouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
            await Wait(() => window.InboxList.ContextMenu.IsOpen);
            var rename = window.InboxList.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "編輯綽號…"));
            Check(rename.IsEnabled, "native right-click opens existing nickname action");
            var editDialog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => {
                var dialog = Windows.OfType<Window>().FirstOrDefault(w => w.Title == "編輯綽號");
                if (dialog is null) return;
                timer.Stop(); FindDndVisuals<TextBox>(dialog).Single().Text = "本機暱稱測試";
                Click(FindDndVisuals<Button>(dialog).Single(b => Equals(b.Content, "儲存"))); editDialog.TrySetResult();
            };
            timer.Start(); rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); window.InboxList.ContextMenu.IsOpen = false;
            await editDialog.Task.WaitAsync(deadline.Token);
            await Wait(() => window.SmokeSelectedEntry?.DisplayName == "本機暱稱測試");
            Check(window.ConversationTitle.Text == "本機暱稱測試" && window.SmokeSelectedEntry?.Endpoint == "10.0.0.21", "nickname updates list/header while keeping target IP");
            Capture(window, Path.Combine(output, "nickname.png"));
            await window.SmokeOpenEntryAsync(e => e.ConversationId == principal.Value);
            Check(window.History.Rows.All(r => r.RawText != delivered.RawText) && window.History.Rows.Count == 2, "switching conversations preserves deletion and remaining retry-delivered message");
            Capture(window, Path.Combine(output, "recovered-history.png"));
            await window.PrepareForShutdownAsync(); window.DetachRuntime(); window.Close(); window = null;
            await runtime.DisposeAsync(); runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            Check(await runtime.Worker.Run(s => s.GetRosterSnapshot().Single().Entry.DisplayName == "本機暱稱測試" && s.ListClassroomMessages(principal).Items.Count == 2 && s.ListClassroomPendingMessages(principal).Count == 0), "real runtime restart retains nickname and deletions");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
            try { if (window is not null) { await window.PrepareForShutdownAsync(); window.DetachRuntime(); window.Close(); } }
            catch (Exception ex) { failure ??= ex.ToString(); }
            if (runtime is not null) await runtime.DisposeAsync(); if (peer is not null) await peer.DisposeAsync();
        }
        File.WriteAllText(Path.Combine(output, "message-recovery-result.json"), JsonSerializer.Serialize(new { passed = failure is null, checks, failure,
            loopbackOnly = true, dailyProfileUsed = false, nativeNicknameRightClick = true, routedSendAndDeleteActions = true,
            pending = new[] { "physical classroom network", "two physical Windows devices", "IME and physical mixed-DPI acceptance" } }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
