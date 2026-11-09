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
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr ContactsForegroundWindow();

    private const string ContactsOldIp = ContactsUiSmokeSeed.OldIp, ContactsIncomingIp = "10.251.232.11", ContactsManualIp = "10.251.232.12";

    // This transport exists only inside the explicit smoke command. Socket traffic stays on
    // loopback; the store sees a declared synthetic private source (product contacts reject
    // loopback). It does not relax endpoint, session or product contact validation.
    private sealed class ContactsSmokeSink(ChatRuntime runtime) : IClassroomTransportSink
    {
        internal string SyntheticSource { get; set; } = ContactsOldIp;
        internal Action<LegacyDeliveryNotice>? LegacyStored { get; set; }
        public async Task<ClassroomTransportResponse?> OnMessageAsync(string source, string local, ClassroomRequest request, Func<bool> current, CancellationToken token) =>
            await await runtime.Worker.Run(s => new ClassroomStoreSink(s).OnMessageAsync(SyntheticSource, local, request, current, token));
        public async Task<ClassroomTransportResponse?> OnReceiptAsync(string source, string local, ClassroomRequest request, Func<bool> current, CancellationToken token) =>
            await await runtime.Worker.Run(s => new ClassroomStoreSink(s).OnReceiptAsync(SyntheticSource, local, request, current, token));
        public async Task<ClassroomTransportResponse?> OnLegacyDirectAsync(string source, DirectMessage request, Func<bool> current, CancellationToken token) =>
            await await runtime.Worker.Run(s => {
                var sink = new ClassroomStoreSink(s);
                sink.LegacyMessageStored += notice => LegacyStored?.Invoke(notice);
                return sink.OnLegacyDirectAsync(SyntheticSource, request, current, token);
            });
    }

    private static ContextMenu[] ContactsOpenMenus() => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Where(source => source.RootVisual is not null).SelectMany(source => SmokeVisuals<ContextMenu>(source.RootVisual!)).Where(menu => menu.IsOpen).Distinct().ToArray();
    private static bool ContactsIsDeleteItem(MenuItem item) =>
        (item.Header as string)?.StartsWith("從這台電腦刪除", StringComparison.Ordinal) == true ||
        (item.Header as string)?.StartsWith("停止傳送並刪除", StringComparison.Ordinal) == true;
    private static MenuItem ContactsDeleteItem(ContextMenu menu) => menu.Items.OfType<MenuItem>().Single(ContactsIsDeleteItem);
    private static async Task<ContextMenu> ContactsOpenMessageMoreAsync(MessageHistoryView history, MessageView message,
        Func<Func<bool>, Func<string>, Task> wait)
    {
        MessageCard[] cards = []; Button[] buttons = []; Button? clicked = null; ContextMenu? opened = null;
        string lastObserved = "not polled";
        string CurrentItems() => "cards=" + cards.Length + "; buttons=[" + string.Join(", ", buttons.Select(button =>
            $"{button.Content} ({System.Windows.Automation.AutomationProperties.GetName(button)}; enabled={button.IsEnabled})")) +
            "]; open menus=[" + string.Join("; ", ContactsOpenMenus().Select(menu =>
                "target=" + (menu.PlacementTarget as Button)?.Content + ": " + string.Join(", ", menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())))) + "]";
        string ActualItems() => "last observed: " + lastObserved + "; current: " + CurrentItems();
        bool Observe(bool ready) { lastObserved = CurrentItems(); return ready; }
        history.ScrollIntoView(message);
        await wait(() => {
            // MessageCard.Build replaces Content on an action/delivery refresh. A realized card
            // can still have an old visual subtree until WPF applies its next layout/template.
            history.UpdateLayout();
            buttons = [];
            cards = SmokeVisuals<MessageCard>(history).Where(card => ReferenceEquals(card.Message, message)).ToArray();
            if (cards.Length > 1) throw new InvalidOperationException("Ambiguous message card: " + ActualItems());
            if (cards.Length == 0) return Observe(false);
            var card = cards.Single(); card.ApplyTemplate(); card.UpdateLayout();
            if (card.Content is not DependencyObject body) return Observe(false);
            buttons = SmokeVisuals<Button>(body).ToArray();
            var candidates = buttons.Where(button => System.Windows.Automation.AutomationProperties.GetName(button) == "訊息操作").ToArray();
            if (candidates.Length > 1) throw new InvalidOperationException("Ambiguous message more button: " + ActualItems());
            if (candidates.Length == 0) return Observe(false);
            var more = candidates.Single();
            if (!more.IsLoaded || !more.IsVisible || !more.IsEnabled || more.ActualWidth <= 0 || more.ActualHeight <= 0) return Observe(false);
            if (!ReferenceEquals(clicked, more))
            {
                if (clicked is not null) foreach (var stale in ContactsOpenMenus().Where(menu => menu.PlacementTarget == clicked)) stale.IsOpen = false;
                more.Focus(); more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); clicked = more;
            }
            var menus = ContactsOpenMenus().Where(menu => menu.PlacementTarget == clicked && menu.ActualWidth > 0 && menu.ActualHeight > 0 &&
                menu.Items.OfType<MenuItem>().Count(ContactsIsDeleteItem) == 1 &&
                menu.Items.OfType<MenuItem>().Count(item => Equals(item.Header, "複製整則原文")) == 1).ToArray();
            if (menus.Length > 1) throw new InvalidOperationException("Ambiguous message action menu: " + ActualItems());
            if (menus.Length == 0) return Observe(false);
            opened = menus.Single(); return Observe(true);
        }, ActualItems);
        return opened ?? throw new InvalidOperationException("Message action menu missing after wait: " + ActualItems());
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
    private static bool ContactsSameBitmap(ImageSource? actual, BitmapSource expected)
    {
        if (actual is not BitmapSource bitmap || bitmap.PixelWidth != expected.PixelWidth || bitmap.PixelHeight != expected.PixelHeight) return false;
        var left = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var right = new FormatConvertedBitmap(expected, PixelFormats.Bgra32, null, 0);
        int stride = checked(left.PixelWidth * 4);
        byte[] first = new byte[checked(stride * left.PixelHeight)], second = new byte[first.Length];
        left.CopyPixels(first, stride, 0); right.CopyPixels(second, stride, 0);
        return first.SequenceEqual(second);
    }

    private async Task<int> RunContactsUiSmokeAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory); UiTestOptions.AssertSafeDataDirectory(output); Directory.CreateDirectory(output);
        string profile = Path.Combine(output, "contacts-profile-" + Guid.NewGuid().ToString("N"));
        var options = UiTestOptions.Parse(["--ui-test-data", profile])!;
        var source = Settings.ForUiTest(options.DataDirectory) with { ClassRosterInitialized = true, AutoStartOnLogin = false };
        var hooks = new ContactsUiSmokeHooks(); var checks = new List<string>(); var captures = new List<string>();
        bool foregroundVerified = false, backgroundReadGuardVerified = false, removalRefreshFailureExercised = false; object? foregroundObservation = null;
        Window? foregroundCover = null;
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
        async Task Wait(Func<bool> condition, string label, Func<string>? details = null)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); step.CancelAfter(TimeSpan.FromSeconds(12));
            try { while (!condition()) await Task.Delay(25, step.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(label + (details is null ? "" : "; " + details())); }
        }
        void Shot(string name, FrameworkElement? element = null, bool exactFileName = false)
        {
            string file = exactFileName ? name + ".png" : $"{captures.Count + 1:D2}-{name}.png"; CaptureContactsElement(element ?? window!, Path.Combine(output, file)); captures.Add(file);
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void ReadOnlyTools(string stage)
        {
            Check(!window!.AttachFileButton.IsEnabled && !window.InsertCodeButton.IsEnabled && !window.CodeLanguage.IsEnabled && !window.StickerButton.IsEnabled,
                stage + " read-only attachment/code/sticker entrypoints are disabled");
            window.AttachFileButton.ApplyTemplate(); window.UpdateLayout();
            var border = window.AttachFileButton.Template.FindName("border", window.AttachFileButton) as Border;
            Check(border is not null && border.Opacity <= 0.5 && window.AttachFileButton.Cursor == System.Windows.Input.Cursors.Arrow,
                stage + " disabled paperclip has a real dimmed template and normal cursor");
        }
        ByteArrayContent Packet(byte[] bytes) { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"); return content; }
        InboxRowView Row(string ip) => window!.InboxList.Items.OfType<InboxRowView>().Single(row => row.Entry.Endpoint == ip);
        void RightClick(FrameworkElement element)
        {
            element.BringIntoView(); window!.UpdateLayout(); DndSetForegroundWindow(new WindowInteropHelper(window).Handle);
            var point = element.PointToScreen(new Point(Math.Min(40, element.ActualWidth / 2), element.ActualHeight / 2));
            if (DndProcessAt(point) != Environment.ProcessId) throw new InvalidOperationException("Synthetic right-click target is not our smoke window.");
            SetCursorPos((int)point.X, (int)point.Y); MouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero); MouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
        }
        void Reachable(Button button, string label)
        {
            button.BringIntoView(); window!.UpdateLayout();
            Check(button.IsVisible && button.ActualWidth > 0 && button.ActualHeight > 0 && button.IsEnabled, label + " is visible, sized and enabled before activation");
        }
        async Task<ContextMenu> TitleMenu()
        {
            Reachable(window!.ConversationMenuButton, "conversation title more");
            Click(window.ConversationMenuButton);
            await Wait(() => ContactsOpenMenus().Any(menu => menu.PlacementTarget == window.ConversationMenuButton && menu.ActualWidth > 0 && menu.ActualHeight > 0), "title menu did not open");
            return ContactsOpenMenus().Single(menu => menu.PlacementTarget == window.ConversationMenuButton);
        }
        async Task InvokeAction(string prefix, Func<bool> completed)
        {
            var menu = await TitleMenu(); var item = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string)?.StartsWith(prefix, StringComparison.Ordinal) == true);
            Check(item.IsEnabled, prefix + " action enabled"); menu.IsOpen = false; hooks.ContactAction = null; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var action = hooks.ContactAction ?? throw new InvalidOperationException("Contact action was not observed on the isolated window.");
            await Wait(() => {
                if (!action.IsCompleted) return false;
                action.GetAwaiter().GetResult(); // Completed only: propagate a real failure, never block the dispatcher.
                return completed();
            }, prefix + " UI state did not update");
        }
        async Task Action(string ip, string prefix, Func<bool> completed)
        {
            await window!.SmokeOpenEntryAsync(entry => entry.Endpoint == ip);
            await InvokeAction(prefix, completed);
        }
        async Task<string> CancellationState()
        {
            var stored = await runtime!.Worker.Run(store => new {
                roster = store.GetRosterSnapshot(), teacher = store.GetClassroomTeacherIp(),
                drafts = store.GetClassroomInboxSnapshot().Entries.Select(entry => new { entry.Principal, entry.Draft }).ToArray()
            });
            return JsonSerializer.Serialize(new { ui = window!.SmokeCancellationState(), stored,
                diskTeacher = Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp });
        }
        async Task CancelAction(string ip, string prefix, string promptFragment, string stage)
        {
            await window!.SmokeOpenEntryAsync(entry => entry.Endpoint == ip);
            await window.SmokeFlushContactDraftAsync();
            await Wait(() => window.SmokeContactsViewIdle, "cancellation baseline view did not settle");
            string before = await CancellationState(); int prompts = hooks.Prompts.Count;
            hooks.NextPromptAnswer = (promptFragment, MessageBoxResult.No);
            await InvokeAction(prefix, () => hooks.NextPromptAnswer is null && hooks.Prompts.Count == prompts + 1 && window.SmokeContactsViewIdle);
            Check(hooks.Prompts[^1].Answer == MessageBoxResult.No && before == await CancellationState(),
                stage + " No preserves complete lists, selection, memory/disk/DB teacher and all draft text/epochs/revisions");
            Shot(stage + "-cancel-unchanged");
        }
        static (string Text, bool Enabled)[] MenuItems(ContextMenu menu) => menu.Items.Cast<object>().Select(item => item switch {
            MenuItem entry => ((string)entry.Header, entry.IsEnabled), Separator => ("#separator", false),
            _ => throw new InvalidOperationException("Unexpected contact menu item type")
        }).ToArray();
        async Task ContactMenus(string stage, bool teacher = false, bool blocked = false, bool removed = false)
        {
            await Wait(() => window!.SmokeContactsViewIdle, "contact menu view did not settle");
            var list = teacher ? window!.TeachersList : window!.InboxList;
            var row = list.Items.OfType<InboxRowView>().Single(item => item.Entry.Id == window.SmokeSelectedEntry!.Id);
            list.ScrollIntoView(row); window.UpdateLayout();
            ListBoxItem? container = null;
            await Wait(() => (container = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem) is { ActualWidth: > 0, ActualHeight: > 0 }, "contact right-click row not realized");
            (string Text, bool Enabled)[] expected = [
                ("編輯本機備註…", !removed), ("編輯 IP…", !removed && !blocked), ("確認為一般聯絡人…", !removed && !blocked),
                (teacher ? "取消老師身分…" : "設為老師…", !removed && !blocked), ("移除聯絡人…", !removed && !blocked),
                ("#separator", false), (blocked ? "解除封鎖…" : "封鎖…", !removed), ("#separator", false), ("清除此對話…", true)
            ];
            RightClick(container!);
            await Wait(() => list.ContextMenu.IsOpen && list.ContextMenu.ActualWidth > 0, "actual contact right-click menu missing");
            var right = MenuItems(list.ContextMenu);
            Check(right.SequenceEqual(expected), "CT-12 " + stage + " right-click has the complete expected labels/order/separators/disabled states");
            Shot(stage + "-right-click", list.ContextMenu); list.ContextMenu.IsOpen = false;
            var title = await TitleMenu();
            Check(MenuItems(title).SequenceEqual(expected) && MenuItems(title).SequenceEqual(right), "CT-12 " + stage + " title menu matches the complete contract");
            Shot(stage + "-title-menu", title); title.IsOpen = false;
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
            var add = SmokeVisuals<Button>(window!).Single(button => Equals(button.Content, "＋ 新增對象"));
            Reachable(add, "add contact");
            DndSetForegroundWindow(new WindowInteropHelper(window!).Handle);
            DndClick(add.PointToScreen(new Point(add.ActualWidth / 2, add.ActualHeight / 2)));
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
        Task<ContextMenu> More(MessageView message) => ContactsOpenMessageMoreAsync(window!.History, message,
            (condition, details) => Wait(condition, "message more menu/button/items did not become ready", details));
        try
        {
            AssertProfile(source); Check(source.Save(), "isolated Settings saved"); ContactsUiSmokeSeed.CreateV13(profile, UiTestOptions.AssertSafeDataDirectory);
            runtime = await ChatRuntime.OpenAsync(new(source, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            // Start only the loopback receiver, never outbox/read-receipt/classroom LAN loops.
            runtime.Listener.Start(); if (runtime.Attachments is { } files) files.Port = runtime.Listener.Port;
            window = new(source); window.InstallContactsUiSmokeHooks(profile, hooks, runtime);
            await window.AttachRuntimeAsync(runtime, runtime.Listener.Port); window.SetReceiverState(true, runtime.Listener.Port, null);
            window.Show(); window.Activate();
            await Wait(() => window.IsActive && window.History.ActualHeight > 0 && hooks.Prompts.Count(p => p.Title == "已讀回條") == 1 && window.SmokeReadNoticeIdle, "native window/upgrade notice not ready");
            Check(await runtime.Worker.Run(s => s.GetReadReceiptPolicy().Enabled && s.GetReadReceiptPreferences().NoticeShown), "RR-02 actual v13 upgrade enables receipts and persists one notice");
            Check(window.InboxList.Items.OfType<InboxRowView>().Count(row => row.Entry.Endpoint == ContactsOldIp) == 1, "CT-02 upgrade retains one unknown history before new ingress");
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsOldIp);
            Check(window.History.Rows.Any(row => row.RawText == "合成舊版歷史：保留這則訊息") && window.Draft.Text == "合成升級前草稿", "CT-02 actual WPF preserves history and draft after migration");
            window.SettingsPane.Visibility = Visibility.Visible; window.UpdateLayout();
            double Top(FrameworkElement element) => element.TranslatePoint(new Point(), window).Y;
            Check(window.ReadReceiptSettingsGroup.Children.IndexOf(window.ReadReceiptToggle) == 0 && window.ReadReceiptSettingsGroup.Children.IndexOf(window.ReadReceiptHelp) == 1 &&
                Top(window.ReadReceiptHelp) >= Top(window.ReadReceiptToggle) + window.ReadReceiptToggle.ActualHeight &&
                Top(window.ClearAttachmentCacheButton) >= Top(window.ReadReceiptHelp) + window.ReadReceiptHelp.ActualHeight &&
                Top(window.AttachmentCacheHelp) >= Top(window.ClearAttachmentCacheButton) + window.ClearAttachmentCacheButton.ActualHeight,
                "settings receipt explanation is directly below its toggle, before cache button and separate attachment help");
            Shot("upgrade-notice-once-and-settings"); window.SettingsPane.Visibility = Visibility.Collapsed;
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
            Check(await runtime.Worker.Run(s => s.GetRosterSnapshot().Single(row => row.Entry.Endpoint == ContactsIncomingIp).Source == "auto" && s.ListClassroomMessages(s.ResolveClassroomPrincipal(ContactsIncomingIp)!.Value).Items.Count == 1), "CT-01 real stored automatic contact and deduplicated body");
            window.UpdateLayout();
            var avatars = SmokeVisuals<Image>(window.InboxList).Where(image => image.DataContext is InboxRowView).ToArray();
            Check(avatars.Length == window.InboxList.Items.Count && avatars.All(image => {
                var entry = ((InboxRowView)image.DataContext).Entry;
                int index = NekoChat.Core.Presentation.PeerAvatarPolicy.PresetIndex(entry.PeerId ?? entry.RosterId, entry.StudentNumber ?? entry.DisplayName, CatArtwork.KnownPresets.Length);
                return ContactsSameBitmap(image.Source, CatArtwork.Preset(CatArtwork.KnownPresets[index]));
            }), "CT-14 actual list avatar pixels match stable identity mapping and existing coat assets, not presence state"); Shot("incoming-one-auto-contact");
            await AddContact(false);
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsOldIp);
            Check(window.SmokeSelectedEntry?.Endpoint != ContactsManualIp, "CT-11 starts on a different conversation before duplicate add");
            await AddContact(true);
            Check(await runtime.Worker.Run(s => s.GetRosterSnapshot().Count(row => row.Entry.Endpoint == ContactsManualIp) == 1), "CT-11 duplicate never creates a second roster row");
            await ContactMenus("contact");
            await Action(ContactsManualIp, "設為老師", () => window.TeachersList.Items.Count == 1 && !window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsManualIp));
            Check(await runtime.Worker.Run(s => s.GetClassroomTeacherIp() == ContactsManualIp) && Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp == ContactsManualIp, "TC-01/03 one teacher and persisted Settings projection"); Shot("teacher-single-pinned");
            await ContactMenus("teacher", teacher: true);
            await CancelAction(ContactsIncomingIp, "設為老師", "將取代目前的老師", "TC-02-teacher-replacement");
            await Action(ContactsIncomingIp, "設為老師", () => window.TeachersList.Items.OfType<InboxRowView>().SingleOrDefault()?.Entry.Endpoint == ContactsIncomingIp && window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsManualIp));
            Check(window.TeachersList.Items.Count == 1 && Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp == ContactsIncomingIp && hooks.Prompts.Any(p => p.Text.Contains("將取代目前的老師", StringComparison.Ordinal)), "TC-02/03 replacement returns previous teacher to classmates and projects Settings");
            // A1 may already have read the original message while establishing its stable baseline.
            // Create a fresh unread control under a real same-process foreground cover window.
            foregroundCover = new Window { Owner = window, Title = "合成背景已讀守衛驗證", Width = 260, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBlock { Text = "合成測試：主聊天窗目前不是前景", Margin = new Thickness(12) } };
            foregroundCover.Show(); foregroundCover.Activate();
            var coverHandle = new WindowInteropHelper(foregroundCover).Handle; DndSetForegroundWindow(coverHandle);
            await Wait(() => ContactsForegroundWindow() == coverHandle && !window.IsActive, "RR-06 could not establish a genuine background control");
            await runtime.Worker.Run(store => store.ReceiveClassroomMessage(ContactsIncomingIp, new(Guid.NewGuid().ToString("D"), "合成前景已讀驗證：背景時保留未讀"), () => true));
            window.DataChanged();
            await Wait(() => window.SmokeContactsViewIdle && window.History.Rows.Any(row => row.RawText == "合成前景已讀驗證：背景時保留未讀"), "background receive/view did not settle");
            Check(!window.IsActive && window.TeachersList.Items.OfType<InboxRowView>().Single().Entry.TotalUnread == 1,
                "RR-06 negative control keeps the new message unread while another real window owns foreground");
            backgroundReadGuardVerified = true; Shot("teacher-background-unread-control");
            foregroundCover.Close(); foregroundCover = null;
            window.Activate(); var mainHandle = new WindowInteropHelper(window).Handle;
            bool activationRequested = DndSetForegroundWindow(mainHandle);
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsIncomingIp);
            await Wait(() => ContactsForegroundWindow() == mainHandle && window.IsActive && window.SmokeContactsViewIdle &&
                window.TeachersList.Items.OfType<InboxRowView>().Single().Entry.TotalUnread == 0,
                "RR-06 foreground teacher open did not clear unread", () => JsonSerializer.Serialize(new { activationRequested, nativeForegroundMatches = ContactsForegroundWindow() == mainHandle, view = window.SmokeReadState }));
            foregroundVerified = true;
            foregroundObservation = new { activationRequested, nativeForegroundMatches = ContactsForegroundWindow() == mainHandle, view = window.SmokeReadState };
            Check(window.TeachersList.Items.OfType<InboxRowView>().Single().BadgeVisibility == Visibility.Collapsed,
                "RR-06 actual foreground opened teacher conversation clears unread and hides its badge after view idle");
            var metadata = SmokeVisuals<TextBlock>(window.History).Where(block => block.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "MetadataText").ToArray();
            Check(metadata.Length > 0 && metadata.All(block => !block.Text.TrimEnd().EndsWith("·", StringComparison.Ordinal)), "message metadata renders no orphan separator");
            Shot("teacher-replaced");
            await runtime.Worker.Run(s => s.ReceiveClassroomMessage(ContactsManualIp, new(Guid.NewGuid().ToString("D"), "合成正文：測試訊息選單"), () => true));
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsManualIp);
            var stored = window.History.Rows.Single(row => row.RawText == "合成正文：測試訊息選單"); var card = await Card(stored);
            var moreMenu = await More(stored); Check(ContactsDeleteItem(moreMenu).IsEnabled, "MD-01 visible message more menu offers local deletion"); Shot("message-more-delete", moreMenu); moreMenu.IsOpen = false;
            card = await Card(stored);
            RightClick(SmokeVisuals<TextBox>(card).Single(box => box.Text == stored.RawText));
            await Wait(() => ContactsOpenMenus().Any(menu => menu.ActualWidth > 0 && menu.ActualHeight > 0 && menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "從這台電腦刪除…"))), "body right-click did not expose local delete");
            var bodyMenu = ContactsOpenMenus().Single(menu => menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "從這台電腦刪除…")));
            Check(ContactsDeleteItem(bodyMenu).IsEnabled, "MD-01 native body right-click exposes enabled local deletion"); Shot("message-body-right-click", bodyMenu); bodyMenu.IsOpen = false;
            window.Draft.Text = "合成傳送中狀態"; await window.SmokeFlushContactDraftAsync();
            var attempt = await runtime.Worker.Run(s => { var p = s.ResolveClassroomPrincipal(ContactsManualIp)!.Value; var d = s.GetClassroomDraftState(p); return (AttemptStarted)s.BeginClassroomSend(p, d.RawText!, d.Epoch, d.Revision); });
            await window.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsManualIp);
            var pending = window.History.Rows.Single(row => row.AttemptId == attempt.MessageId);
            await Card(pending);
            pending.RefreshActions(); // Exercise the real deferred Content rebuild before inspecting its new button tree.
            var disabledMenu = await More(pending); var disabled = ContactsDeleteItem(disabledMenu);
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
            Button? cancelTransfer = null;
            await Wait(() => {
                window.UpdateLayout();
                var buttons = SmokeVisuals<Button>(window.History).Where(button => Equals(button.Content, "取消傳輸")).ToArray();
                if (buttons.Length != 1) return false;
                cancelTransfer = buttons.Single();
                return cancelTransfer.IsVisible && cancelTransfer.ActualWidth > 0 && cancelTransfer.ActualHeight > 0 && cancelTransfer.IsEnabled;
            }, "cancel transfer button did not become reachable");
            Reachable(cancelTransfer!, "cancel transfer"); Click(cancelTransfer!);
            await Wait(() => window.History.Rows.Any(row => row.Sequence == incomingFile.LocalSeq && row.Attachment?.State == "cancelled"), "synthetic transfer cancellation not reflected");
            var removedId = window.SmokeSelectedEntry!.Id;
            if (!ClassroomPrincipalKey.TryParse(window.SmokeSelectedEntry.ConversationId, out var removedPrincipal)) throw new InvalidOperationException("Synthetic removed principal missing.");
            await window.SmokeFlushContactDraftAsync();
            var retainedDraft = await runtime.Worker.Run(s => s.GetClassroomDraftState(removedPrincipal));
            Check(window.InboxFilter.SelectedIndex == 0 && retainedDraft.RawText == "合成傳送中狀態", "CT-13 starts in general contacts with a nonempty persisted draft");
            await CancelAction(ContactsManualIp, "移除聯絡人", "只從這台電腦的名單移除", "CT-13-remove");
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
                "CT-13 archive rendering/flush preserves exact SQLite draft epoch/revision/text and memory buffer");
            window.UpdateLayout();
            var archivedRetries = SmokeVisuals<Button>(window.History).Where(button => Equals(button.Content, "重試傳送")).ToArray();
            Check(!window.RetryAttempt.IsEnabled && archivedRetries.Length > 0 && archivedRetries.All(button => !button.IsEnabled), "CT-13 all archived retry-send controls are disabled");
            Check(window.ConversationTitle.Text == "合成同學 C 的本機備註（已從名單移除）" && window.ConversationMeta.Text == "已從名單移除，不能發送，可查看對話", "CT-13 archive title/subtitle are explicit read-only text");
            await ContactMenus("archive", removed: true);
            ReadOnlyTools("CT-13 archive");
            await Wait(() => SmokeVisuals<Button>(window.History).Any(button => Equals(button.Content, "停止後續重試") && button.IsEnabled), "archived failed attempt lost its local stop action");
            Check(window.CancelAttemptButton.IsEnabled && window.History.Rows.Any(row => row.AttemptId == attempt.MessageId && row.CancelSend is not null),
                "CT-13 local stop remains available for an old failed attempt; read-only does not authorize a new send"); Shot("removed-archive-readonly-prompt-draft-retained");
            window.InboxFilter.SelectedIndex = 0;
            await Wait(() => window.InboxList.Items.OfType<InboxRowView>().All(row => row.Entry.Id != removedId), "CT-13 removed row leaked back into general filter");
            await CancelAction(ContactsOldIp, "封鎖", "封鎖後不再接收", "BK-01-block");
            await Action(ContactsOldIp, "封鎖", () => window.InboxFilter.SelectedIndex == 3 && window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsOldIp && row.Entry.Blocked));
            Check(window.Draft.IsReadOnly && !window.SendButton.IsEnabled && window.Draft.Text.Length == 0 && window.DraftPlaceholder.IsVisible && window.DraftPlaceholder.Text.Contains("已封鎖", StringComparison.Ordinal), "BK-01/06 blocked conversation is separate with an explicit read-only prompt"); ReadOnlyTools("BK-01/06 blocked"); Shot("blocked-readonly");
            Check(window.ConversationTitle.Text == ContactsOldIp && window.ConversationMeta.Text == "已封鎖，歷史唯讀", "BK-06 blocked title/subtitle remain explicit");
            await ContactMenus("blocked", blocked: true);
            await Action(ContactsOldIp, "解除封鎖", () => window.InboxFilter.SelectedIndex == 0 && window.SmokeContactsViewIdle && window.SmokeSelectedEntry is { Blocked: false } && window.Draft.IsEnabled);
            Check(window.Draft.Text == "合成升級前草稿", "CT-13/BK-06 restoring an editable contact restores the retained draft projection"); Shot("unblocked-retained-draft-restored");
            await Action(ContactsOldIp, "封鎖", () => window.InboxFilter.SelectedIndex == 3 && window.SmokeContactsViewIdle && window.SmokeSelectedEntry is { Blocked: true } && window.Draft.IsReadOnly);
            await Action(ContactsIncomingIp, "封鎖", () => window.TeachersList.Items.Count == 0 && window.SetTeacherButton.IsVisible && window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Endpoint == ContactsIncomingIp && row.Entry.Blocked));
            Check(hooks.Prompts.Any(p => p.Text.Contains("取消此對象的老師身分", StringComparison.Ordinal)) && Settings.LoadWithStatus(source.PersistencePath).Value.TeacherIp == "", "BK-07 teacher block warns, clears pinned teacher and Settings projection"); ReadOnlyTools("BK-07 blocked teacher"); Shot("blocked-teacher-add-teacher-visible");
            // Empty filtered-list branch: use the real contact form and removal menu, not synthetic selection state.
            window.InboxFilter.SelectedIndex = 0;
            const string lastIp = "10.251.232.13";
            await AddContact(false, lastIp);
            var lastId = window.SmokeSelectedEntry!.Id;
            Check(window.InboxList.Items.Count == 1, "CT-13 empty-list fixture has exactly one active classmate");
            hooks.FailRemovalRefreshOnce = true;
            await Action(lastIp, "移除聯絡人", () => window.SmokeContactsViewIdle && !hooks.FailRemovalRefreshOnce && !hooks.FailNextInboxRead &&
                window.SmokeSelectedEntry is null && window.Status.Text == "已移到封存區，可在篩選切換查看");
            Check(window.InboxList.Items.OfType<InboxRowView>().Any(row => row.Entry.Id == lastId && !row.Entry.Removed) &&
                window.SmokeSelectedEntry is null && !window.SendButton.IsEnabled && window.StorageStatus.Text.Contains("NCD-", StringComparison.Ordinal),
                "N1 injected refresh failure retains a demonstrably stale list but never reselects the removed id");
            removalRefreshFailureExercised = true; Shot("removal-refresh-failure-no-stale-selection");
            await window.SmokeRefreshContactsAsync();
            await Wait(() => window.SmokeContactsViewIdle && window.InboxList.Items.Count == 0, "inbox did not recover after isolated read failure");
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
                "CT-13 last contact remains readable only through explicit archive selection"); ReadOnlyTools("CT-13 last archive"); Shot("removed-last-contact-explicit-archive");
            await window.PrepareForShutdownAsync(); window.DetachRuntime(); window.Hide(); await runtime.DisposeAsync(); runtime = null;
            source = Settings.LoadWithStatus(source.PersistencePath).Value; AssertProfile(source);
            runtime = await ChatRuntime.OpenAsync(new(source, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            int promptsBeforeReopen = hooks.Prompts.Count;
            window = new(source); window.InstallContactsUiSmokeHooks(profile, hooks, runtime); await window.AttachRuntimeAsync(runtime, 0); window.Show(); window.Activate();
            System.Diagnostics.Stopwatch? quiet = null;
            await Wait(() => {
                if (hooks.Prompts.Count != promptsBeforeReopen) throw new InvalidOperationException("RR-02 unexpected prompt during/after reopen");
                if (!window.IsActive || !window.SmokeReadNoticeIdle || !window.SmokeContactsViewIdle || !window.InboxFilter.IsVisible) { quiet = null; return false; }
                quiet ??= System.Diagnostics.Stopwatch.StartNew(); return quiet.ElapsedMilliseconds >= 500;
            }, "RR-02 reopened view failed to stay idle/prompt-free continuously for 500ms");
            Check(hooks.Prompts.Count(p => p.Title == "已讀回條") == 1 && await runtime.Worker.Run(s => s.GetReadReceiptPreferences().NoticeShown), "RR-02 reopening same upgraded profile never shows a second notice");
            Check(await runtime.Worker.Run(s => s.GetClassroomDraftState(removedPrincipal)) == retainedDraft,
                "CT-13 originally removed draft survives dispose/reopen with exact epoch/revision/text");
            window.SettingsPane.Visibility = Visibility.Visible; Shot("reopen-no-second-notice");
            Check(hooks.NextPromptAnswer is null && hooks.Prompts.Count(prompt => prompt.Answer == MessageBoxResult.No) == 3, "all three distinct cancellation answers were consumed exactly once");
            await RunReactionPreferencesUiChecksAsync(output, Check, (name, element) =>
                Shot(name, element, exactFileName: ClassroomReactions.All.Any(reaction => name == "reaction-" + reaction.Id)),
                (condition, label) => Wait(condition, label), deadline.Token);
            Check(captures.Count >= 18, "all native UI stages produced synthetic screenshots");
        }
        catch (Exception error)
        {
            failure = error.ToString(); Console.Error.WriteLine(failure);
            foregroundObservation ??= new { view = window?.SmokeReadState, nativeForegroundMatches = window is not null && ContactsForegroundWindow() == new WindowInteropHelper(window).Handle };
            try { if (window?.IsVisible == true) Shot("failure"); } catch { }
        }
        finally
        {
            try { foregroundCover?.Close(); foreach (var menu in ContactsOpenMenus()) menu.IsOpen = false; if (window is not null) await window.PrepareForShutdownAsync(); window?.DetachRuntime(); window?.Hide(); if (synthetic is not null) await synthetic.DisposeAsync(); if (runtime is not null) await runtime.DisposeAsync(); }
            catch (Exception error) { failure ??= error.ToString(); }
            SetCursorPos(originalCursor.X, originalCursor.Y); DndAllowedProcesses.Remove(Environment.ProcessId);
        }
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new {
            passed = failure is null, checks, captures, failure, prompts = hooks.Prompts.Select(p => new { p.Title, p.Text, answer = p.Answer.ToString() }),
            foregroundVerified, backgroundReadGuardVerified, foregroundObservation, removalRefreshFailureExercised, hooksInstalledBeforeAttach = true,
            seedSchemaVersion = 13, dailyProfileUsed = false, loopbackOnly = true, syntheticSourceMapping = true,
            backgroundSendLoopsStarted = false, dialogsAutoAnswered = true, attemptStateSeeded = true, attachmentTransferStateSeeded = true,
            captureKind = "Real WPF RenderTargetBitmap96; prompts auto-answered, not native MessageBox captures; not physical DPI evidence",
            sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA"), runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID")
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
}
