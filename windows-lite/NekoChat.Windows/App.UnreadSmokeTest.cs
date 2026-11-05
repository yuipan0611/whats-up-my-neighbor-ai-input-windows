using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    private async Task<int> RunUnreadSmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        UiTestOptions.AssertSafeDataDirectory(output);
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(output, "unread-fixture-" + Guid.NewGuid().ToString("N"));
        var settings = Settings.ForUiTest(fixture) with { ClassRosterInitialized = true, AutoStartOnLogin = false };
        var checks = new List<string>(); string? failure = null; object? diagnostic = null;
        ChatRuntime? runtime = null; ChatWindow? window = null;
        const string first = "127.0.0.2", second = "127.0.0.3";
        var principal = ClassroomPrincipalKey.ForSourceIp(first);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Until(Func<bool> condition, string label)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { while (!condition()) await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(label); }
            checks.Add(label);
        }
        try
        {
            runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            await runtime.Worker.Run(store => {
                store.ReceiveClassroomMessage(first, new(Guid.NewGuid().ToString("D"), "第一則未讀"), () => true);
                store.ReceiveLegacyMessage(new("第二則未讀（舊版）", null), store.GetLegacyReceiveContext(first), () => true);
                store.ReceiveClassroomMessage(second, new(Guid.NewGuid().ToString("D"), "其他同學未讀"), () => true);
                return true;
            });
            window = new(settings);
            await window.AttachRuntimeAsync(runtime, 0);
            window.Show(); window.Activate();
            await Until(() => window.IsActive && window.History.ActualHeight > 0, "native chat window is active and laid out");
            InboxRowView Row(string ip) => window.InboxList.Items.OfType<InboxRowView>().Single(row => row.Entry.Endpoint == ip);
            Check(Row(first).Badge == "2" && Row(first).BadgeVisibility == Visibility.Visible, "sidebar initially shows two unread messages");
            await Task.Delay(400);
            Check(Row(first).Entry.TotalUnread == 2, "automatic history preview does not mark unopened chat read");
            // Enter also opens the initially selected preview via the real list keyboard handler.
            window.InboxList.SelectedItem = Row(first);
            window.InboxList.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Until(() => Row(first).Entry.TotalUnread == 0, "opening visible chat clears its two unread messages");
            Check(Row(first).BadgeVisibility == Visibility.Collapsed, "zero unread hides the sidebar badge");
            Check(Row(second).Entry.TotalUnread == 1 && window.SmokeUnreadTotal == 1, "other conversation and aggregate unread remain correct");
            Check(await runtime.Worker.Run(store => store.ListClassroomMessages(principal).Items.Single().LocalReadAt is not null &&
                store.GetClassroomInboxSnapshot().Entries.Single(entry => entry.Principal == principal).TotalUnread == 0), "classroom and legacy local reads persisted in SQLite");
            Capture(window, Path.Combine(output, "unread-cleared.png"));

            // Hold the real worker while a read is queued, then let a resized viewport's timer fire.
            // A stale transaction must be rejected AND the latest visible view must still be read.
            window.Hide();
            const string delayed = "儲存忙碌時打開的訊息";
            await runtime.Worker.Run(store => store.ReceiveClassroomMessage(first, new(Guid.NewGuid().ToString("D"), delayed), () => true));
            window.DataChanged();
            await Until(() => Row(first).Entry.TotalUnread == 1, "delayed-read fixture has one unread");
            window.ShowChat(first);
            await Until(() => window.History.Rows.Any(row => row.RawText == delayed), "delayed-read message is actually loaded");
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var busyStore = runtime.Worker.Run(_ => {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException("Synthetic worker gate");
                    return true;
                });
                try
                {
                    await entered.Task;
                    await Until(() => window.SmokeReadBusy, "viewport read waits behind busy storage worker");
                    window.Height -= 8;
                    await Task.Delay(500);
                }
                finally { release.Set(); await busyStore; }
            }
            await Until(() => Row(first).Entry.TotalUnread == 0, "latest viewport is read after stale busy-worker read is rejected");

            window.Hide();
            await runtime.Worker.Run(store => store.ReceiveClassroomMessage(first, new(Guid.NewGuid().ToString("D"), "隱藏時收到"), () => true));
            window.DataChanged();
            await Until(() => Row(first).Entry.TotalUnread == 1, "new message increments unread while chat is hidden");
            await Task.Delay(400);
            Check(Row(first).Entry.TotalUnread == 1, "hidden chat does not clear unread");
            window.ShowChat(first);
            await Until(() => Row(first).Entry.TotalUnread == 0, "reopening the same chat from the pet path clears new unread");

            window.ReadReceiptToggle.IsChecked = false;
            window.ReadReceiptToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                while (await runtime.Worker.Run(store => store.GetReadReceiptPolicy().Enabled)) await Task.Delay(25, timeout.Token);
            window.Hide();
            await runtime.Worker.Run(store => store.ReceiveClassroomMessage(first, new(Guid.NewGuid().ToString("D"), "關閉回條仍需本機已讀"), () => true));
            window.DataChanged();
            await Until(() => Row(first).Entry.TotalUnread == 1, "receipt-off message appears as unread before viewing");
            window.ShowChat(first);
            await Until(() => Row(first).Entry.TotalUnread == 0, "local unread clears while outbound read receipts are disabled");
            Check(await runtime.Worker.Run(store => store.ListPendingClassroomReceipts().Count == 0), "receipt-off reads do not queue outbound receipts");

            // A long conversation must retain offscreen unread; scrolling, not merely selecting, reads those rows.
            await runtime.Worker.Run(store => {
                for (int i = 0; i < 30; i++)
                    store.ReceiveClassroomMessage(second, new(Guid.NewGuid().ToString("D"), $"尚未看過的第 {i} 則訊息"), () => true);
                return true;
            });
            window.DataChanged();
            await Until(() => Row(second).Entry.TotalUnread == 31, "new messages in another chat remain unread");
            window.InboxList.SelectedItem = Row(second);
            await Until(() => Row(second).Entry.TotalUnread is > 0 and < 31, "opening long chat reads visible rows and preserves offscreen unread");
            long remaining = Row(second).Entry.TotalUnread;
            var historyScroll = SmokeVisuals<ScrollViewer>(window.History).First();
            historyScroll.ScrollToTop();
            await Until(() => Row(second).Entry.TotalUnread < remaining, "scrolling to older messages updates unread without reopening");
            Capture(window, Path.Combine(output, "partly-read.png"));

            await window.PrepareForShutdownAsync(); window.DetachRuntime(); window.Hide();
            await runtime.DisposeAsync(); runtime = null;
            runtime = await ChatRuntime.OpenAsync(new(settings, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            Check(await runtime.Worker.Run(store => {
                var snapshot = store.GetClassroomInboxSnapshot();
                return snapshot.Entries.Single(entry => entry.Principal == principal).TotalUnread == 0 &&
                    snapshot.Entries.Single(entry => entry.Endpoint == second).TotalUnread is > 0 and < 31;
            }), "restart preserves read marks and remaining offscreen unread");
            window = null;
        }
        catch (Exception ex)
        {
            failure = ex.ToString(); diagnostic = window?.SmokeReadState;
            if (window?.IsVisible == true) Capture(window, Path.Combine(output, "unread-failure.png"));
            Console.Error.WriteLine(failure);
        }
        finally
        {
            try
            {
                if (window is not null) await window.PrepareForShutdownAsync();
                window?.DetachRuntime(); window?.Hide();
                if (runtime is not null) await runtime.DisposeAsync();
            }
            catch (Exception ex) { failure ??= ex.ToString(); }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "unread-result.json"), JsonSerializer.Serialize(new {
            passed = failure is null, checks, failure, diagnostic, loopbackOnly = true, dailyProfileUsed = false,
            captureKind = "WPF RenderTargetBitmap96, not physical desktop DPI evidence"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
