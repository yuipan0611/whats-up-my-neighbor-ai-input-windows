using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core;
using NekoChat.Core.Attachments;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using NekoChat.TestFixtures;

namespace NekoChat.Windows;

public partial class App
{
    private const string ContactsOldIp = ContactsUiSmokeSeed.OldIp, ContactsIncomingIp = "10.251.232.11", ContactsManualIp = "10.251.232.12";

    // This transport exists only inside the explicit smoke command. Socket traffic stays on
    // loopback; the store sees a declared synthetic private source (product contacts reject
    // loopback). It does not relax endpoint, session or product contact validation.
    private sealed class ContactsSmokeSink(ChatRuntime runtime) : IClassroomTransportSink
    {
        internal string SyntheticSource { get; set; } = ContactsOldIp;
        public async Task<ClassroomTransportResponse?> OnMessageAsync(string source, string local, ClassroomRequest request, Func<bool> current, CancellationToken token) =>
            await await runtime.Worker.Run(s => new ClassroomStoreSink(s).OnMessageAsync(SyntheticSource, local, request, current, token));
        public async Task<ClassroomTransportResponse?> OnReceiptAsync(string source, string local, ClassroomRequest request, Func<bool> current, CancellationToken token) =>
            await await runtime.Worker.Run(s => new ClassroomStoreSink(s).OnReceiptAsync(SyntheticSource, local, request, current, token));
        public async Task<ClassroomTransportResponse?> OnLegacyDirectAsync(string source, DirectMessage request, Func<bool> current, CancellationToken token) =>
            await await runtime.Worker.Run(s => new ClassroomStoreSink(s).OnLegacyDirectAsync(SyntheticSource, request, current, token));
    }

    private static ContextMenu[] ContactsOpenMenus() => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Where(source => source.RootVisual is not null).SelectMany(source => SmokeVisuals<ContextMenu>(source.RootVisual!)).Where(menu => menu.IsOpen).Distinct().ToArray();
    private static MenuItem ContactsDeleteItem(ContextMenu menu) => menu.Items.OfType<MenuItem>().Single(item =>
        (item.Header as string)?.StartsWith("從這台電腦刪除", StringComparison.Ordinal) == true ||
        (item.Header as string)?.StartsWith("停止傳送並刪除", StringComparison.Ordinal) == true);
    private static async Task<ContextMenu> ContactsOpenMessageMoreAsync(MessageCard card, CancellationToken cancellation)
    {
        var more = SmokeVisuals<Button>(card).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "訊息操作");
        more.Focus(); more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await UntilAsync(() => ContactsOpenMenus().Any(menu => menu.ActualWidth > 0 && menu.ActualHeight > 0 && menu.Items.OfType<MenuItem>().Any(item => (item.Header as string)?.Contains("刪除", StringComparison.Ordinal) == true)), timeout.Token);
        return ContactsOpenMenus().Single(menu => menu.Items.OfType<MenuItem>().Any(item => (item.Header as string)?.Contains("刪除", StringComparison.Ordinal) == true));
    }
    private static void CaptureContactsElement(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Native evidence element is not laid out.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }

    private async Task<int> RunContactsUiSmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory); UiTestOptions.AssertSafeDataDirectory(output); Directory.CreateDirectory(output);
        string profile = Path.Combine(output, "contacts-profile-" + Guid.NewGuid().ToString("N"));
        var options = UiTestOptions.Parse(["--ui-test-data", profile])!;
        var source = Settings.ForUiTest(options.DataDirectory) with { ClassRosterInitialized = true, AutoStartOnLogin = false };
        var hooks = new ContactsUiSmokeHooks(); var checks = new List<string>(); var captures = new List<string>();
        string? failure = null; ChatRuntime? runtime = null; ChatWindow? window = null; ClassroomTcpListener? synthetic = null;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        string resultFile = Path.Combine(output, "contacts-ui-result.json");
        using var watchdog = new System.Threading.Timer(_ => {
            try { File.WriteAllText(resultFile, JsonSerializer.Serialize(new { passed = false, failure = "Contacts UI global deadline expired", dailyProfileUsed = false })); }
            finally { Environment.Exit(1); }
        }, null, TimeSpan.FromSeconds(115), Timeout.InfiniteTimeSpan);
        GetCursorPos(out var originalCursor); DndAllowedProcesses.Add(Environment.ProcessId);
        void AssertProfile(Settings value)
        {
            if (value.PersistencePath is not { } path || !string.Equals(Path.GetFullPath(path), options.SettingsFilePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(ChatRuntime.RootDirectory(value, true)), profile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Smoke refuses any settings/runtime profile mismatch before opening data.");
        }
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Wait(Func<bool> condition, string label)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); step.CancelAfter(TimeSpan.FromSeconds(12));
            try { while (!condition()) await Task.Delay(25, step.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(label); }
        }
        void Shot(string name, FrameworkElement? element = null)
        {
            string file = $"{captures.Count + 1:D2}-{name}.png"; CaptureContactsElement(element ?? window!, Path.Combine(output, file)); captures.Add(file);
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ByteArrayContent Packet(byte[] bytes) { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"); return content; }
        InboxRowView Row(string ip) => window!.InboxList.Items.OfType<InboxRowView>().Single(row => row.Entry.Endpoint == ip);
        void RightClick(FrameworkElement element)
        {
            element.BringIntoView(); window!.UpdateLayout(); DndSetForegroundWindow(new WindowInteropHelper(window).Handle);
            var point = element.PointToScreen(new Point(Math.Min(40, element.ActualWidth / 2), element.ActualHeight / 2));
            if (DndProcessAt(point) != Environment.ProcessId) throw new InvalidOperationException("Synthetic right-click target is not our smoke window.");
            SetCursorPos((int)point.X, (int)point.Y); MouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero); MouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
        }
        async Task<ContextMenu> TitleMenu()
        {
            Click(window!.ConversationMenuButton);
            await Wait(() => ContactsOpenMenus().Any(menu => menu.PlacementTarget == window.ConversationMenuButton && menu.ActualWidth > 0 && menu.ActualHeight > 0), "title menu did not open");
            return ContactsOpenMenus().Single(menu => menu.PlacementTarget == window.ConversationMenuButton);
        }
        async Task Action(string ip, string prefix, Func<bool> completed)
        {
            await window!.SmokeOpenEntryAsync(entry => entry.Endpoint == ip);
            var menu = await TitleMenu(); var item = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string)?.StartsWith(prefix, StringComparison.Ordinal) == true);
            Check(item.IsEnabled, prefix + " action enabled"); menu.IsOpen = false; hooks.ContactAction = null; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var action = hooks.ContactAction ?? throw new InvalidOperationException("Contact action was not observed on the isolated window.");
            await Wait(() => {
                if (!action.IsCompleted) return false;
                action.GetAwaiter().GetResult(); // Completed only: propagate a real failure, never block the dispatcher.
                return completed();
            }, prefix + " UI state did not update");
        }
        async Task AddContact(bool duplicate, string ip = ContactsManualIp)
        {
            hooks.DriveDialog = async dialog => {
                var input = SmokeVisuals<TextBox>(dialog).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "輸入對方 IP 位址");
                input.Text = ip;
                SmokeVisuals<TextBox>(dialog).Single(box => box != input).Text = "合成同學 C 的本機備註";
                Shot(duplicate ? "duplicate-input" : "add-ip-note", dialog);
                Click(SmokeVisuals<Button>(dialog).Single(button => Equals(button.Content, "新增對象")));
                if (duplicate)
                {
                    await Wait(() => SmokeVisuals<Button>(dialog).Any(button => Equals(button.Content, "開啟既有對話") && button.IsVisible), "duplicate must offer existing conversation");
                    Check(SmokeVisuals<TextBlock>(dialog).Any(text => text.Text.Contains("已在名單中", StringComparison.Ordinal)), "CT-11 duplicate explains existing IP");
                    Shot("duplicate-existing-action", dialog);
                    Click(SmokeVisuals<Button>(dialog).Single(button => Equals(button.Content, "開啟既有對話")));
                }
            };
            Click(SmokeVisuals<Button>(window!).Single(button => Equals(button.Content, "＋ 新增對象")));
            await Wait(() => window!.SmokeSelectedEntry?.Endpoint == ip && window.ConversationTitle.Text == "合成同學 C 的本機備註", "add/duplicate did not select saved contact");
            Check(Row(ip).Name == "合成同學 C 的本機備註", duplicate ? "CT-11 duplicate opens existing note" : "CT-10 one form saves IP and local note");
            Shot(duplicate ? "duplicate-opened-existing" : "added-selected");
        }
        async Task<MessageCard> Card(MessageView message)
        {
            window!.History.ScrollIntoView(message); window.UpdateLayout();
            await Wait(() => window.SmokeContactsViewIdle && SmokeVisuals<MessageCard>(window.History).Any(card => ReferenceEquals(card.Message, message)), "message card/read/autosave view not settled");
            return SmokeVisuals<MessageCard>(window.History).Single(card => ReferenceEquals(card.Message, message));
        }
        try
        {
            AssertProfile(source); Check(source.Save(), "isolated Settings saved"); ContactsUiSmokeSeed.CreateV13(profile, UiTestOptions.AssertSafeDataDirectory);
            runtime = await ChatRuntime.OpenAsync(new(source, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            // Start only the loopback receiver, never outbox/read-receipt/classroom LAN loops.
            runtime.Listener.Start(); if (runtime.Attachments is { } files) files.Port = runtime.Listener.Port;
            window = new(source); await window.AttachRuntimeAsync(runtime, runtime.Listener.Port);
            window.InstallContactsUiSmokeHooks(profile, hooks); window.SetReceiverState(true, runtime.Listener.Port, null);
            window.Show(); window.Activate();
            await Wait(() => window.IsActive && window.History.ActualHeight > 0 && hooks.Prompts.Count(p => p.Title == "已讀回條") == 1 && window.SmokeReadNoticeIdle, "native window/upgrade notice not ready");
            Check(await runtime.Worker.Run(s => s.GetReadReceiptPolicy().Enabled && s.GetReadReceiptPreferences().NoticeShown), "RR-02 actual v13 upgrade enables receipts and persists one notice");
            Check(window.InboxList.Items.OfType<InboxRowView>().Count(row => row.Entry.Endpoint == ContactsOldIp) == 1, "CT-02 upgrade retains one unknown history before new ingress");
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsOldIp);
            Check(window.History.Rows.Any(row => row.RawText == "合成舊版歷史：保留這則訊息") && window.Draft.Text == "合成升級前草稿", "CT-02 actual WPF preserves history and draft after migration");
            window.SettingsPane.Visibility = Visibility.Visible; Shot("upgrade-notice-once-and-settings"); window.SettingsPane.Visibility = Visibility.Collapsed;
            // Read state is still exercised locally; outbound LAN loops were not started.
            var mapped = new ContactsSmokeSink(runtime);
            synthetic = new(new ClassroomEndpointAdapter(mapped, capabilities: ["msg.plain", "receipt.read"]), new() { BindAddress = IPAddress.Loopback, Port = 0 }); synthetic.Start();
            async Task Incoming(string text)
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
                string id = Guid.NewGuid().ToString("D");
                for (int resend = 0; resend < 2; resend++)
                {
                    using var hello = await http.PostAsync($"http://127.0.0.1:{synthetic.Port}/nekochat/classroom/v1/hello", Packet(ClassroomPacketWriter.HelloRequest(ClassroomPacketWriter.NewToken())), deadline.Token);
                    Check(hello.IsSuccessStatusCode, "CT-01 loopback hello accepted " + resend);
                    using var reply = JsonDocument.Parse(await hello.Content.ReadAsByteArrayAsync(deadline.Token)); string session = reply.RootElement.GetProperty("session").GetString()!;
                    using var response = await http.PostAsync($"http://127.0.0.1:{synthetic.Port}/nekochat/classroom/v1/message", Packet(ClassroomPacketWriter.MessageSend(id, session, "127.0.0.1", text)), deadline.Token);
                    Check(response.IsSuccessStatusCode, "CT-01 synthetic loopback request acknowledged " + resend);
                }
                window.DataChanged();
            }
            await Incoming("合成升級後來訊，與舊歷史合併");
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsOldIp);
            Check(Row(ContactsOldIp).Entry.RosterId is not null && window.History.Rows.Count(row => row.RawText == "合成舊版歷史：保留這則訊息") == 1 && window.Draft.Text == "合成升級前草稿", "CT-02 incoming atomically merges old history/draft into one actual WPF contact"); Shot("old-history-merged-one-contact");
            Check(!window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsIncomingIp), "CT-01 stranger absent before incoming request");
            mapped.SyntheticSource = ContactsIncomingIp;
            await Incoming("合成陌生來訊，重送也只能一條");
            await synthetic.DisposeAsync(); synthetic = null;
            await Wait(() => window.InboxList.Items.OfType<InboxRowView>().Count(row => row.Entry.Endpoint == ContactsIncomingIp) == 1, "CT-01 auto contact not projected");
            Check(await runtime.Worker.Run(s => s.GetRosterSnapshot().Single(row => row.Entry.Endpoint == ContactsIncomingIp).Source == "auto" && s.ListClassroomMessages(s.ResolveClassroomPrincipal(ContactsIncomingIp)!.Value).Items.Count == 1), "CT-01 real stored automatic contact and deduplicated body"); Shot("incoming-one-auto-contact");
            await AddContact(false);
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsOldIp);
            Check(window.SmokeSelectedEntry?.Endpoint != ContactsManualIp, "CT-11 starts on a different conversation before duplicate add");
            await AddContact(true);
            Check(await runtime.Worker.Run(s => s.GetRosterSnapshot().Count(row => row.Entry.Endpoint == ContactsManualIp) == 1), "CT-11 duplicate never creates a second roster row");
            window.UpdateLayout(); var container = (ListBoxItem)window.InboxList.ItemContainerGenerator.ContainerFromItem(Row(ContactsManualIp)); RightClick(container);
            await Wait(() => window.InboxList.ContextMenu.IsOpen && window.InboxList.ContextMenu.ActualWidth > 0, "actual contact right-click menu missing");
            var rightItems = window.InboxList.ContextMenu.Items.OfType<MenuItem>().Select(item => ((string)item.Header, item.IsEnabled)).ToArray(); Shot("contact-right-click", window.InboxList.ContextMenu); window.InboxList.ContextMenu.IsOpen = false;
            var title = await TitleMenu(); Check(rightItems.SequenceEqual(title.Items.OfType<MenuItem>().Select(item => ((string)item.Header, item.IsEnabled))), "CT-12 actual right-click/title menus have identical items and enabled states"); Shot("contact-title-menu", title); title.IsOpen = false;
            await Action(ContactsManualIp, "設為老師", () => window.TeachersList.Items.Count == 1 && !window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsManualIp));
            Check(await runtime.Worker.Run(s => s.GetClassroomTeacherIp() == ContactsManualIp) && Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp == ContactsManualIp, "TC-01/03 one teacher and persisted Settings projection"); Shot("teacher-single-pinned");
            await Action(ContactsIncomingIp, "設為老師", () => window.TeachersList.Items.OfType<InboxRowView>().SingleOrDefault()?.Entry.Endpoint == ContactsIncomingIp && window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsManualIp));
            Check(window.TeachersList.Items.Count == 1 && Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp == ContactsIncomingIp && hooks.Prompts.Any(p => p.Text.Contains("將取代目前的老師", StringComparison.Ordinal)), "TC-02/03 replacement returns previous teacher to classmates and projects Settings"); Shot("teacher-replaced");
            await runtime.Worker.Run(s => s.ReceiveClassroomMessage(ContactsManualIp, new(Guid.NewGuid().ToString("D"), "合成正文：測試訊息選單"), () => true));
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsManualIp);
            var stored = window.History.Rows.Single(row => row.RawText == "合成正文：測試訊息選單"); var card = await Card(stored);
            var moreMenu = await ContactsOpenMessageMoreAsync(card, deadline.Token); Check(ContactsDeleteItem(moreMenu).IsEnabled, "MD-01 visible message more menu offers local deletion"); Shot("message-more-delete", moreMenu); moreMenu.IsOpen = false;
            RightClick(SmokeVisuals<TextBox>(card).Single(box => box.Text == stored.RawText));
            await Wait(() => ContactsOpenMenus().Any(menu => menu.ActualWidth > 0 && menu.ActualHeight > 0 && menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "從這台電腦刪除…"))), "body right-click did not expose local delete");
            var bodyMenu = ContactsOpenMenus().Single(menu => menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "從這台電腦刪除…")));
            Check(ContactsDeleteItem(bodyMenu).IsEnabled, "MD-01 native body right-click exposes enabled local deletion"); Shot("message-body-right-click", bodyMenu); bodyMenu.IsOpen = false;
            window.Draft.Text = "合成傳送中狀態"; await window.SmokeFlushContactDraftAsync();
            var attempt = await runtime.Worker.Run(s => { var p = s.ResolveClassroomPrincipal(ContactsManualIp)!.Value; var d = s.GetClassroomDraftState(p); return (AttemptStarted)s.BeginClassroomSend(p, d.RawText!, d.Epoch, d.Revision); });
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsManualIp);
            var pending = window.History.Rows.Single(row => row.AttemptId == attempt.MessageId);
            var disabledMenu = await ContactsOpenMessageMoreAsync(await Card(pending), deadline.Token); var disabled = ContactsDeleteItem(disabledMenu);
            Check(!disabled.IsEnabled && (disabled.ToolTip as string)?.Contains("傳送中", StringComparison.Ordinal) == true, "MD-03 real sending attempt disables deletion with a reason"); Shot("sending-delete-disabled", disabledMenu); disabledMenu.IsOpen = false;
            await runtime.Worker.Run(s => s.CompleteClassroomAttempt(attempt.MessageId, new FailedOutcome("offline"), null, attempt.TryCount));
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsManualIp);
            string staged = Path.Combine(profile, "synthetic-draft.txt"); await File.WriteAllTextAsync(staged, "only synthetic attachment bytes", deadline.Token);
            await window.SmokeStageContactAttachmentAsync(staged);
            Check(window.RemoveAttachmentButton.IsVisible && Equals(window.RemoveAttachmentButton.Content, "移除附件"), "MD-05 unsent draft says remove attachment"); Shot("draft-remove-attachment"); Click(window.RemoveAttachmentButton);
            await Wait(() => !window.AttachmentDraftPanel.IsVisible, "draft removal did not complete");
            var incomingFile = await runtime.Worker.Run(s => { var record = ((ChatStore)s).ReceiveAttachmentOffer(ContactsManualIp, 8768, Guid.NewGuid().ToString("D"), new AttachmentFile(Guid.NewGuid().ToString("N"), "synthetic-transfer.txt", 1, new string('a', 64)), "", new string('b', 64), () => true).Record!; ((ChatStore)s).SetAttachmentState(record, "receiving"); return record; });
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsManualIp);
            var transferCard = await Card(window.History.Rows.Single(row => row.Sequence == incomingFile.LocalSeq && row.Attachment is not null));
            Check(SmokeVisuals<Button>(transferCard).Any(button => Equals(button.Content, "取消傳輸")), "MD-05 active attachment says cancel transfer, distinct from local deletion/draft removal"); Shot("transfer-cancel-distinct");
            Click(SmokeVisuals<Button>(transferCard).Single(button => Equals(button.Content, "取消傳輸")));
            await Wait(() => window.History.Rows.Any(row => row.Sequence == incomingFile.LocalSeq && row.Attachment?.State == "cancelled"), "synthetic transfer cancellation not reflected");
            var removedId = window.SmokeSelectedEntry!.Id;
            if (!ClassroomPrincipalKey.TryParse(window.SmokeSelectedEntry.ConversationId, out var removedPrincipal)) throw new InvalidOperationException("Synthetic removed principal missing.");
            await window.SmokeFlushContactDraftAsync();
            var retainedDraft = await runtime.Worker.Run(s => s.GetClassroomDraftState(removedPrincipal));
            Check(window.InboxFilter.SelectedIndex == 0 && retainedDraft.RawText == "合成傳送中狀態", "CT-13 starts in general contacts with a nonempty persisted draft");
            await Action(ContactsManualIp, "移除聯絡人", () => window.InboxFilter.SelectedIndex == 0 && window.SmokeContactsViewIdle &&
                window.InboxList.Items.OfType<InboxRowView>().All(row => row.Entry.Id != removedId) &&
                window.SmokeSelectedEntry?.Id == window.InboxList.Items.OfType<InboxRowView>().FirstOrDefault()?.Entry.Id &&
                window.Status.Text == "已移到封存區，可在篩選切換查看" && window.Status.IsVisible);
            Check(window.InboxList.Items.Count > 0 && window.SmokeSelectedEntry is { Removed: false, Blocked: false } && window.Draft.IsEnabled,
                "CT-13 removal stays in general filter, selects its first row and shows archive notice"); Shot("removed-stays-general-first-contact");
            window.InboxFilter.SelectedIndex = 2;
            await Wait(() => window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Id == removedId && row.Entry.Removed), "CT-13 archive filter does not expose the stable removed principal");
            // Removed entries intentionally have no endpoint. Select the real list row by stable ID, never by old IP.
            window.InboxList.SelectedItem = window.InboxList.Items.OfType<InboxRowView>().Single(row => row.Entry.Id == removedId);
            await Wait(() => window.SmokeContactsViewIdle && window.SmokeSelectedEntry?.Id == removedId && window.Draft.IsReadOnly &&
                window.History.Rows.Any(row => row.RawText == stored.RawText), "CT-13 archive selection/read-only history did not settle");
            Check(window.SmokeSelectedEntry is { Removed: true, Endpoint: null } && window.Draft.IsReadOnly && !window.Draft.IsEnabled && !window.SendButton.IsEnabled &&
                window.Draft.Text.Length == 0 && window.DraftPlaceholder.IsVisible && window.DraftPlaceholder.Text == "這是封存對話，僅供查看歷史；草稿已保留。",
                "CT-13 archived composer shows only a read-only prompt, never the retained editable-looking draft");
            await window.SmokeFlushContactDraftAsync();
            Check(await runtime.Worker.Run(s => s.GetClassroomDraftState(removedPrincipal)) == retainedDraft && window.SmokeContactDraft(removedPrincipal.Value) == retainedDraft.RawText,
                "CT-13 archive rendering/flush preserves exact SQLite draft epoch/revision/text and memory buffer"); Shot("removed-archive-readonly-prompt-draft-retained");
            window.InboxFilter.SelectedIndex = 0;
            await Wait(() => window.InboxList.Items.OfType<InboxRowView>().All(row => row.Entry.Id != removedId), "CT-13 removed row leaked back into general filter");
            await Action(ContactsOldIp, "封鎖", () => window.InboxFilter.SelectedIndex == 3 && window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsOldIp && row.Entry.Blocked));
            Check(window.Draft.IsReadOnly && !window.SendButton.IsEnabled && window.Draft.Text.Length == 0 && window.DraftPlaceholder.IsVisible && window.DraftPlaceholder.Text.Contains("已封鎖", StringComparison.Ordinal), "BK-01/06 blocked conversation is separate with an explicit read-only prompt"); Shot("blocked-readonly");
            await Action(ContactsOldIp, "解除封鎖", () => window.InboxFilter.SelectedIndex == 0 && window.SmokeContactsViewIdle && window.SmokeSelectedEntry is { Blocked: false } && window.Draft.IsEnabled);
            Check(window.Draft.Text == "合成升級前草稿", "CT-13/BK-06 restoring an editable contact restores the retained draft projection"); Shot("unblocked-retained-draft-restored");
            await Action(ContactsOldIp, "封鎖", () => window.InboxFilter.SelectedIndex == 3 && window.SmokeContactsViewIdle && window.SmokeSelectedEntry is { Blocked: true } && window.Draft.IsReadOnly);
            await Action(ContactsIncomingIp, "封鎖", () => window.TeachersList.Items.Count == 0 && window.SetTeacherButton.IsVisible && window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsIncomingIp && row.Entry.Blocked));
            Check(hooks.Prompts.Any(p => p.Text.Contains("取消此對象的老師身分", StringComparison.Ordinal)) && Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp == "", "BK-07 teacher block warns, clears pinned teacher and Settings projection"); Shot("blocked-teacher-add-teacher-visible");
            // Empty filtered-list branch: use the real contact form and removal menu, not synthetic selection state.
            window.InboxFilter.SelectedIndex = 0;
            const string lastIp = "10.251.232.13";
            await AddContact(false, lastIp);
            var lastId = window.SmokeSelectedEntry!.Id;
            Check(window.InboxList.Items.Count == 1, "CT-13 empty-list fixture has exactly one active classmate");
            await Action(lastIp, "移除聯絡人", () => window.SmokeContactsViewIdle && window.InboxFilter.SelectedIndex == 0 &&
                window.InboxList.Items.Count == 0 && window.SmokeSelectedEntry is null && window.Status.Text == "已移到封存區，可在篩選切換查看");
            Check(window.InboxEmptyPanel.IsVisible && window.History.Rows.Count == 0 && window.Draft.Text.Length == 0 && !window.SendButton.IsEnabled,
                "CT-13 removing the last general contact leaves an empty conversation and general list"); Shot("removed-last-contact-general-empty");
            await window.SmokeRefreshContactsAsync();
            Check(window.SmokeSelectedEntry is null && window.InboxFilter.SelectedIndex == 0 && window.InboxList.Items.Count == 0,
                "CT-13 subsequent refresh does not silently reselect an archive in the empty general list");
            window.InboxFilter.SelectedIndex = 2;
            await Wait(() => window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Id == lastId && row.Entry.Removed), "CT-13 last removed contact missing from archive");
            window.InboxList.SelectedItem = window.InboxList.Items.OfType<InboxRowView>().Single(row => row.Entry.Id == lastId);
            await Wait(() => window.SmokeContactsViewIdle && window.SmokeSelectedEntry?.Id == lastId && window.Draft.IsReadOnly, "CT-13 last archived conversation did not settle");
            Check(!window.Draft.IsEnabled && !window.SendButton.IsEnabled && window.Draft.Text.Length == 0 && window.DraftPlaceholder.IsVisible,
                "CT-13 last contact remains readable only through explicit archive selection"); Shot("removed-last-contact-explicit-archive");
            await window.PrepareForShutdownAsync(); window.DetachRuntime(); window.Hide(); await runtime.DisposeAsync(); runtime = null;
            source = Settings.LoadWithStatus(source.PersistencePath).Value; AssertProfile(source);
            runtime = await ChatRuntime.OpenAsync(new(source, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            window = new(source); await window.AttachRuntimeAsync(runtime, 0); window.InstallContactsUiSmokeHooks(profile, hooks); window.Show(); window.Activate();
            await Wait(() => window.IsActive && window.SmokeReadNoticeIdle && window.InboxFilter.IsVisible, "reopened native window not ready");
            Check(hooks.Prompts.Count(p => p.Title == "已讀回條") == 1 && await runtime.Worker.Run(s => s.GetReadReceiptPreferences().NoticeShown), "RR-02 reopening same upgraded profile never shows a second notice");
            Check(await runtime.Worker.Run(s => s.GetClassroomDraftState(removedPrincipal)) == retainedDraft,
                "CT-13 originally removed draft survives dispose/reopen with exact epoch/revision/text");
            window.SettingsPane.Visibility = Visibility.Visible; Shot("reopen-no-second-notice");
            Check(captures.Count >= 18, "all native UI stages produced synthetic screenshots");
        }
        catch (Exception error)
        {
            failure = error.ToString(); Console.Error.WriteLine(failure);
            try { if (window?.IsVisible == true) Shot("failure"); } catch { }
        }
        finally
        {
            try { foreach (var menu in ContactsOpenMenus()) menu.IsOpen = false; if (window is not null) await window.PrepareForShutdownAsync(); window?.DetachRuntime(); window?.Hide(); if (synthetic is not null) await synthetic.DisposeAsync(); if (runtime is not null) await runtime.DisposeAsync(); }
            catch (Exception error) { failure ??= error.ToString(); }
            SetCursorPos(originalCursor.X, originalCursor.Y); DndAllowedProcesses.Remove(Environment.ProcessId);
        }
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new {
            passed = failure is null, checks, captures, failure, prompts = hooks.Prompts.Select(p => new { p.Title, p.Text }),
            seedSchemaVersion = 13, dailyProfileUsed = false, loopbackOnly = true, syntheticSourceMapping = true,
            backgroundSendLoopsStarted = false, dialogsAutoAnswered = true, attemptStateSeeded = true, attachmentTransferStateSeeded = true,
            captureKind = "Real WPF RenderTargetBitmap96; prompts auto-answered, not native MessageBox captures; not physical DPI evidence",
            sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA"), runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID")
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
