using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NekoChat.Core;
using NekoChat.Core.Presentation;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using NekoChat.Core.Classroom.Transport;

namespace NekoChat.Windows;

internal sealed record InboxRowView(InboxEntry Entry, string State)
{
    public string Name => Entry.DisplayName;
    public string Initial => Entry.IsTeacher ? "師" : StringInfoInitial(Entry.DisplayName);
    public string Student => Entry.IsTeacher ? "老師" : Entry.StudentNumber is { } number ? "學號 " + number : "";
    public string Badge => Entry.TotalUnread > 99 ? "99+" : Entry.TotalUnread.ToString();
    public Visibility BadgeVisibility => Entry.TotalUnread > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Draft => string.IsNullOrEmpty(Entry.Draft?.RawText) ? "" : "有草稿";
    public string AccessibleLabel => $"{Name}，{Student}，{State}，未讀 {Entry.TotalUnread} 則。{Draft}";
    public Brush DotFill => State == "已安裝／在線" ? new SolidColorBrush(Color.FromRgb(35, 115, 83)) : Brushes.White;
    public Brush DotStroke => State.Contains("不符", StringComparison.Ordinal) || State.Contains("暫停", StringComparison.Ordinal) ? new SolidColorBrush(Color.FromRgb(145, 96, 21)) : State == "已安裝／在線" ? DotFill : new SolidColorBrush(Color.FromRgb(89, 107, 128));
    public DoubleCollection DotDash => State.Contains("未知", StringComparison.Ordinal) || State.Contains("未確認", StringComparison.Ordinal) || State.Contains("未驗證", StringComparison.Ordinal) ? new([2, 1]) : new();
    private static string StringInfoInitial(string name) => name.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(name);
}

public partial class ChatWindow
{
    private ChatRuntime? _runtime;
    private readonly ObservableCollection<InboxRowView> _inbox = [];
    private readonly Dictionary<string, DraftEditBuffer> _drafts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _readTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private InboxSnapshot? _snapshot;
    private InboxEntry? _selected;
    private bool _initializedInbox, _changingSelection, _settingDraft, _userOpened, _showInbox = true, _receiverRunning, _sending, _refreshing, _refreshAgain, _readBusy, _stopping, _closingRequested;
    private long _viewGeneration;
    private long _readPolicyEdit;
    private long _manualFormGeneration;
    private string? _editingRosterId, _manualFormSnapshot;
    private long _selectionRequest;
    private bool _replyLookupPending;
    private long? _beforeMessage, _beforeLegacy;
    private bool _historyLoaded;
    private int _peerPort = DirectLanMessaging.DefaultPort;
    internal event Action? DataRetryRequested;
    internal event Action? IdentityRecoveryRequested;
    private bool _identityRecoveryBusy;
    private void CheckIdentityClick(object sender, RoutedEventArgs e) { if (_runtime?.ArchivedIdentityFixture == true) IdentityRecoveryRequested?.Invoke(); }
    internal void SetIdentityRecoveryStatus(string text, bool busy)
    { _identityRecoveryBusy = busy; IdentityRecoveryStatus.Text = text; CheckIdentityButton.IsEnabled = !busy && _runtime is not null && !_closingRequested; }

    private void InitializeInbox()
    {
        InboxList.ItemsSource = _inbox; _initializedInbox = true;
        InboxList.PreviewMouseLeftButtonUp += async (_, e) => {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(InboxList, source) is ListBoxItem { DataContext: InboxRowView row } &&
                (!_userOpened || _showInbox || _selected?.Id != row.Entry.Id)) await SelectEntryAsync(row.Entry, true);
        };
        InboxList.PreviewKeyDown += async (_, e) => {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && !e.IsRepeat && InboxList.SelectedItem is InboxRowView row)
            { e.Handled = true; await SelectEntryAsync(row.Entry, true); }
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await FlushDraftsAsync(); };
        _readTimer.Tick += async (_, _) => { _readTimer.Stop(); await MarkViewportAsync(); };
        Activated += (_, _) => ViewChanged(); Deactivated += (_, _) => ViewChanged(); StateChanged += (_, _) => ViewChanged(); IsVisibleChanged += (_, _) => ViewChanged();
        History.PreviewMouseDown += (_, _) => { if (_selected is not null) { _userOpened = true; _showInbox = false; ViewChanged(); } };
        UpdateComposer(); UpdateShellLayout();
    }
    internal async Task AttachRuntimeAsync(ChatRuntime runtime, int peerPort)
    {
        _runtime = runtime; ResetRecipientProtocolHints(); ArchivedIdentityPanel.Visibility = runtime.ArchivedIdentityFixture ? Visibility.Visible : Visibility.Collapsed; _peerPort = peerPort; IdentityStatus.Text = runtime.IdentityStatus; StorageStatus.Text = runtime.StartupWarning ?? "";
        var policy = await runtime.Worker.Run(s => s.GetReadReceiptPolicy()); ReadReceiptToggle.IsChecked = policy.Enabled;
        if (runtime.Presence is { } presence) presence.StateChanged += PresenceChanged;
        await RefreshInboxAsync();
    }
    internal void ShowDataFailure(string message)
    { StorageStatus.Text = message; ReloadData.Visibility = Visibility.Visible; UpdateComposer(); }
    internal void DetachRuntime() { _pairingWindow?.CloseSafely(); _pairingSelection = null; if (_runtime?.Presence is { } presence) presence.StateChanged -= PresenceChanged; _runtime = null; ResetRecipientProtocolHints(); ArchivedIdentityPanel.Visibility = Visibility.Collapsed; _receiverRunning = false; ReceiverStatus.Text = "接收暫停，等待本機資料就緒。"; RefreshPairingControls(); UpdateComposer(); }
    internal void DataChanged() { if (!_stopping) _ = RefreshInboxAsync(refreshHistory: true); }
    private void PresenceChanged() => Dispatcher.BeginInvoke(new Action(() => { if (!_stopping) { ApplyInboxFilter(); UpdateHeader(); } }));
    private async Task RefreshInboxAsync(bool refreshHistory = false)
    {
        if (_runtime is null || _stopping) return;
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                if (_runtime.ArchivedIdentityFixture)
                {
                    _snapshot = await _runtime.Worker.Run(s => s.GetInboxSnapshot());
                }
                else
                {
                    var crSnapshot = await _runtime.Worker.Run(s => s.GetClassroomInboxSnapshot());
                    _snapshot = crSnapshot.ToInboxSnapshot();
                }
                if (_stopping) return;
                var previous = _selected; var current = previous?.Id;
                _selected = current is null ? _replyLookupPending ? null : _snapshot.Entries.FirstOrDefault() : _snapshot.Entries.FirstOrDefault(e => e.Id == current);
                bool changed = previous?.Id != _selected?.Id || previous?.ConversationId != _selected?.ConversationId;
                if (changed) { _userOpened = false; _historyLoaded = false; History.Rows.Clear(); SetDraftText(CurrentDraft()?.Text ?? ""); ViewChanged(); }
                ApplyInboxFilter();
                if (_selected is not null)
                {
                    EnsureDraft(_selected); if (changed) SetDraftText(CurrentDraft()?.Text ?? ""); UpdateHeader();
                    if (!_historyLoaded || refreshHistory) await LoadHistoryAsync(older: false, reset: !_historyLoaded);
                }
                else { History.Rows.Clear(); OlderMessages.Visibility = Visibility.Collapsed; HistoryState.Text = "選擇一位同學，或新增名單。"; HistoryState.Visibility = Visibility.Visible; UpdateHeader(); }
                UpdateComposer();
            } while (_refreshAgain && !_stopping);
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { _refreshing = false; }
    }
    private string StateOf(InboxEntry entry)
    {
        if (_runtime?.ArchivedIdentityFixture != true)
        {
            if (entry.Removed) return "已從名單移除，不能發送，可查看對話";
            if (entry.Endpoint is null) return "未設定位址";
            return entry.LegacyUnread > 0 ? "舊版訊息・在線狀態待確認" : "在線狀態待確認";
        }
        if (entry.Pairing is PairingState.Suspended) return "配對已暫停，請重新核對身分";
        if (entry.Pairing is PairingState.Revoked) return "配對已撤銷";
        var presence = entry.RosterId is { } row ? _runtime?.Presence?.GetSnapshot(row) : null;
        if (presence is { AddressConflict: true }) return "位址身分不符，請重新核對";
        if (presence is { Presence: NekoChat.Core.Presence.PresenceStatus.Online }) return "已安裝／在線";
        if (presence is { Presence: NekoChat.Core.Presence.PresenceStatus.Offline }) return "已安裝／離線";
        if (presence is { EndpointResponds: true }) return "App 在此位址回應・身分未驗證";
        if (presence is { Installed: NekoChat.Core.Presence.InstalledStatus.ConfirmedInstalled }) return "已安裝／在線未知";
        return entry.Pairing == PairingState.Paired ? "已配對／在線未知" : entry.PeerId is null && entry.LegacyUnread > 0 ? "舊版・未驗證" : "尚未確認身分與 App 狀態";
    }
    private void ApplyInboxFilter()
    {
        if (!_initializedInbox || _snapshot is null) return;
        string query = InboxSearch.Text.Trim(); bool unread = InboxFilter.SelectedIndex == 1;
        var entries = _snapshot.Entries.Where(e => (!unread || e.TotalUnread > 0) && (query.Length == 0 || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.StudentNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) == true || e.Endpoint?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
        _changingSelection = true;
        try
        {
            _inbox.Clear(); foreach (var entry in entries) _inbox.Add(new(entry, StateOf(entry)));
            InboxList.SelectedItem = _inbox.FirstOrDefault(e => e.Entry.Id == _selected?.Id);
        }
        finally { _changingSelection = false; }
        InboxCount.Text = $"同學 · 未讀 {_snapshot.TotalUnread} · {_snapshot.Entries.Count} 位";
        bool noEntries = _snapshot.Entries.Count == 0;
        InboxEmpty.Text = noEntries ? "尚無同學對話。\n新增名單後，即可開始一對一聊天。" : "沒有符合搜尋或篩選的同學。";
        AddFirstContactButton.Visibility = noEntries ? Visibility.Visible : Visibility.Collapsed;
        InboxEmptyPanel.Visibility = _inbox.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdatePresenceView();
    }
    private void InboxSearchChanged(object sender, TextChangedEventArgs e) => ApplyInboxFilter();
    private void InboxFilterChanged(object sender, SelectionChangedEventArgs e) { if (_initializedInbox) ApplyInboxFilter(); }
    private async void InboxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || InboxList.SelectedItem is not InboxRowView row) return;
        await SelectEntryAsync(row.Entry, userOpened: true);
    }
    private async Task SelectEntryAsync(InboxEntry entry, bool userOpened)
    {
        ++_selectionRequest; _replyLookupPending = false;
        _selected = entry; _userOpened = userOpened; _showInbox = false; _historyLoaded = false; _beforeMessage = _beforeLegacy = null;
        ViewChanged(); History.Rows.Clear(); EnsureDraft(entry); SetDraftText(CurrentDraft()?.Text ?? "");
        RecipientIp.Text = entry.Endpoint ?? ""; UpdateHeader(); UpdateShellLayout(); UpdateComposer(); await LoadHistoryAsync(false, true);
        ViewChanged();
    }
    private async void SelectEndpoint(string? endpoint)
    {
        if (endpoint is null) { _showInbox = _selected is null; UpdateShellLayout(); ViewChanged(); return; }
        InboxEntry? Find(InboxSnapshot? snapshot) => endpoint.StartsWith("peer:", StringComparison.Ordinal) ? snapshot?.Entries.FirstOrDefault(e => e.PeerId == endpoint[5..]) :
            endpoint.StartsWith("legacy-", StringComparison.Ordinal) ? snapshot?.Entries.FirstOrDefault(e => e.LegacyScope?.ContextId == endpoint) : snapshot?.Entries.FirstOrDefault(e => e.Endpoint == endpoint);
        if (Find(_snapshot) is { } entry) { await SelectEntryAsync(entry, true); return; }
        long request = ++_selectionRequest; _replyLookupPending = true; _selected = null; _userOpened = false; _historyLoaded = false; History.Rows.Clear(); SetDraftText(""); UpdateHeader(); UpdateComposer(); ViewChanged();
        if (_runtime is null) { _replyLookupPending = false; return; }
        try
        {
            var snapshot = await _runtime.Worker.Run(s => s.GetInboxSnapshot()); if (request != _selectionRequest) return;
            _snapshot = snapshot; ApplyInboxFilter(); _replyLookupPending = false;
            if (Find(snapshot) is { } found) await SelectEntryAsync(found, true);
            else { _showInbox = true; UpdateShellLayout(); SetStatus("尚無這個來源的對話；請確認名單與來源後再選擇對象。"); }
        }
        catch (Exception ex) { if (request == _selectionRequest) { _replyLookupPending = false; ShowStorageError(ex); } }
    }
    private void EnsureDraft(InboxEntry entry)
    {
        if (entry.ConversationId is { } id && entry.Draft is { } state)
        {
            if (!_drafts.TryGetValue(id, out var buffer)) _drafts[id] = new(state);
            else if (!_sending && !buffer.Dirty && !buffer.Clearing && buffer.Capture() is { } current && (state.Epoch > current.Epoch || state.Epoch == current.Epoch && state.Revision > current.Revision))
            { _drafts[id] = new(state); if (_selected?.ConversationId == id) SetDraftText(state.RawText ?? ""); }
        }
        if (!_historyLoaded) SetDraftText(CurrentDraft()?.Text ?? "");
    }
    private DraftEditBuffer? CurrentDraft() => _selected?.ConversationId is { } id && _drafts.TryGetValue(id, out var buffer) ? buffer : null;
    private void SetDraftText(string text) { _settingDraft = true; try { Draft.Text = text; } finally { _settingDraft = false; } }
    private void DraftEdited(object sender, TextChangedEventArgs e)
    {
        if (!_initializedInbox || _settingDraft) return;
        if (CurrentDraft() is { Clearing: false } buffer) { try { buffer.Edit(Draft.Text); _saveTimer.Stop(); _saveTimer.Start(); } catch (Exception ex) { ShowStorageError(ex); } }
        UpdateComposer();
    }
    internal async Task FlushDraftsAsync()
    {
        if (_runtime is null) return;
        foreach (var pair in _drafts.Where(p => p.Value.Dirty && !p.Value.Clearing).ToArray())
        {
            var ticket = pair.Value.Capture();
            try
            {
                var result = await _runtime.Worker.Run(s => {
                    if (!_runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(pair.Key, out var principal))
                    {
                        var cr = s.SaveClassroomDraft(principal, ticket.Text, ticket.Epoch, ticket.Revision);
                        return new DraftSaveResult(cr.Saved, cr.Reason, cr.CurrentEpoch, cr.CurrentRevision);
                    }
                    return ticket.Persist(s, pair.Key);
                });
                pair.Value.CompleteWrite(ticket, result);
                if (!result.Saved && pair.Value.IsCurrent(ticket) && pair.Value.Dirty)
                    StorageStatus.Text = result.Reason == "draft-too-large" ? "草稿太長無法保存，請縮短。" : "草稿尚未儲存，請重新讀取或保留目前文字後重試。";
            }
            catch (Exception ex) { ShowStorageError(ex); }
        }
        UpdateComposer();
    }
    private void UpdateComposer()
    {
        if (!_initializedInbox) return;
        var buffer = CurrentDraft(); bool attached = _runtime is not null; bool ready = attached && _runtime!.CanSend && (_runtime.ArchivedIdentityFixture ? _selected?.Pairing == PairingState.Paired : _selected?.CanSend == true);
        bool isRemoved = _selected?.Removed == true;
        Draft.IsReadOnly = isRemoved;
        Draft.IsEnabled = !_stopping && !_closingRequested && (attached ? buffer is { Clearing: false } : _receiverRunning) && !isRemoved;
        bool valid = !string.IsNullOrWhiteSpace(Draft.Text) && (_runtime?.MaximumTextBytes is not { } limit || V2Text.Check(Draft.Text, limit) == V2TextProblem.None);
        SendButton.IsEnabled = !_closingRequested && !_sending && _receiverRunning && valid && (attached ? ready && buffer is { Clearing: false } : true);
        TeacherReactions.IsEnabled = ScrollReaction.IsEnabled = CanSendReaction;
        var attempt = _selected?.Attempt; AttemptBanner.Visibility = attempt is null ? Visibility.Collapsed : Visibility.Visible;
        bool cancelling = attempt is not null && _runtime?.IsAttemptCancellationRequested(attempt.MessageId, attempt.TryCount) == true;
        AttemptText.Text = ClassroomSendUiPolicy.DecideAttemptBanner(attempt?.State, cancelling);
        RetryAttempt.IsEnabled = ready && !_sending && attempt is { State: not "sending" } && attempt.RawSnapshot == Draft.Text;
        CancelAttemptButton.IsEnabled = !_closingRequested && attached && !cancelling && attempt is { State: not ("cancelled" or "confirmed" or "superseded") } &&
            (attempt.State != "sending" || _runtime!.CanCancelAttempt(attempt.MessageId, attempt.TryCount));
        ClearConversationButton.IsEnabled = attached && _selected is not null && buffer?.Clearing != true;
        CheckIdentityButton.IsEnabled = _runtime?.ArchivedIdentityFixture == true && !_identityRecoveryBusy && !_closingRequested;
        int byteCount = Encoding.UTF8.GetByteCount(Draft.Text);
        int maxBytes = _runtime?.MaximumTextBytes ?? 65536;
        string count = byteCount > 55000 ? $"{byteCount} / {maxBytes} 字元 · " : "";
        string prompt = !attached ? "Enter 傳送 · Shift＋Enter 換行" :
            buffer is null ? "選擇一位同學後開始聊天" :
            _selected?.Removed == true ? "這位同學已從名單移除，無法傳送訊息。" :
            _selected?.Endpoint is null ? "💡 請先在設定填寫這位同學的 IP 即可傳送" :
            !_runtime!.CanSend ? "教室傳送服務尚未就緒…" :
            !valid && Draft.Text.Length > 0 ? "⚠️ 訊息字數超過上限" :
            "Enter 傳送 · Shift＋Enter 換行";
        DraftStatus.Text = count + prompt;
    }
    private void UpdateHeader()
    {
        ConversationTitle.Text = _selected?.DisplayName ?? "選擇一位同學";
        ConversationMeta.Text = _selected is { } entry ? StateOf(entry) + (entry.Endpoint is { } ip ? (entry.RosterId is null ? " · 最近位址 " : " · 名單 IP ") + ip : "") : "對話紀錄保存在本機";
        bool legacy = CurrentRecipientIsLegacy;
        TrustBanner.Visibility = legacy || History.Rows.Any(r => r.Legacy) ? Visibility.Visible : Visibility.Collapsed;
        TrustBannerText.Text = legacy ? MessageLimitPolicy.LegacyTooLargeNotice + "。傳送前會重新確認版本；舊版確認不代表已儲存或已讀。" :
            _runtime?.ArchivedIdentityFixture == true ? "含舊版・未驗證訊息；位址或名單名稱不代表身分。" : "含舊版訊息；舊版確認不代表對方已儲存或已讀。";
    }
    private async Task LoadHistoryAsync(bool older, bool reset)
    {
        if (_runtime is null || _selected is not { } entry) return; long generation = Volatile.Read(ref _viewGeneration);
        if (reset) { HistoryState.Text = "載入對話中…"; HistoryState.Visibility = Visibility.Visible; }
        bool wasAtEnd = History.Items.Count == 0 || IsAtEnd();
        var oldScroll = FindVisual<ScrollViewer>(History); double oldOffset = oldScroll?.VerticalOffset ?? 0, oldExtent = oldScroll?.ExtentHeight ?? 0; long selection = _selectionRequest;
        try
        {
            long? before = older ? _beforeMessage : null, legacyBefore = older ? _beforeLegacy : null;
            var page = await _runtime.Worker.Run(s => {
                if (!_runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal))
                {
                    var crPage = (!older || before is not null) ? s.ListClassroomMessages(principal, before) : null;
                    var legPage = entry.LegacyScope is { } scope && (!older || legacyBefore is not null) ? s.ListLegacyMessages(scope, legacyBefore) : null;
                    return (Classroom: crPage, Message: (MessagePage?)null, Legacy: legPage);
                }
                return (Classroom: (ClassroomMessagePage?)null,
                    Message: entry.ConversationId is { } conv && (!older || before is not null) ? s.ListMessages(conv, before) : null,
                    Legacy: entry.LegacyScope is { } legScope && (!older || legacyBefore is not null) ? s.ListLegacyMessages(legScope, legacyBefore) : null);
            });
            if (_stopping || generation != Volatile.Read(ref _viewGeneration) || entry.Id != _selected?.Id) return;
            if (reset) History.Rows.Clear();
            var crRows = page.Classroom?.Items.Select(m => new MessageView("cr:" + m.LocalSeq, m.RawText, m.Direction == "outgoing", false, m.LocalSeq, m.CreatedUtc,
                m.Direction == "outgoing" ? (m.DeliveryLabel ?? (m.RemoteReadAt is not null ? "已讀" : m.RemoteStoredAt is not null ? "對方已儲存" : "")) : (m.LocalReadAt is not null ? "本機已讀" : ""))) ?? [];
            var rows = page.Message?.Items.Select(m => new MessageView("v2:" + m.LocalSeq, m.RawText, m.Direction == "outgoing", false, m.LocalSeq, m.CreatedUtc,
                m.Direction == "outgoing" ? m.RemoteReadAt is not null ? "已讀" : "對方已儲存" : m.LocalReadAt is not null ? "本機已讀" : "")) ?? [];
            var legacy = page.Legacy?.Items.Select(m => new MessageView("v1:" + m.LegacySeq, m.RawText, false, true, m.LegacySeq, m.ReceivedUtc, LegacyIncomingLabel + (m.LocalReadAt is not null ? " · 本機已讀" : ""))) ?? [];
            var existing = History.Rows.ToDictionary(m => m.Key, StringComparer.Ordinal); int oldCount = existing.Count;
            foreach (var row in crRows.Concat(rows).Concat(legacy)) { if (existing.TryGetValue(row.Key, out var old)) old.Delivery = row.Delivery; else existing.Add(row.Key, row); }
            var sorted = existing.Values.OrderBy(m => m.Utc, StringComparer.Ordinal).ThenBy(m => m.Legacy).ThenBy(m => m.Sequence).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                if (i < History.Rows.Count && ReferenceEquals(History.Rows[i], sorted[i])) continue;
                int index = History.Rows.IndexOf(sorted[i]); if (index >= 0) History.Rows.Move(index, i); else History.Rows.Insert(i, sorted[i]);
            }
            if (older || reset) { _beforeMessage = page.Classroom?.NextBeforeLocalSeq ?? page.Message?.NextBeforeLocalSeq; _beforeLegacy = page.Legacy?.NextBeforeLegacySeq; }
            OlderMessages.Visibility = _beforeMessage is not null || _beforeLegacy is not null ? Visibility.Visible : Visibility.Collapsed;
            _historyLoaded = true; HistoryState.Text = History.Rows.Count == 0 ? "還沒有訊息" : ""; HistoryState.Visibility = History.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!older && (reset || wasAtEnd)) ScrollLatest(); else if (!older && History.Rows.Count > oldCount) NewMessages.Visibility = Visibility.Visible;
            if (older && oldScroll is not null) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if (selection == _selectionRequest && _selected?.Id == entry.Id) oldScroll.ScrollToVerticalOffset(oldOffset + Math.Max(0, oldScroll.ExtentHeight - oldExtent));
            }));
            UpdateHeader(); QueueViewportRead();
        }
        catch (Exception ex) { HistoryState.Text = "無法讀取對話，請重新讀取。"; HistoryState.Visibility = Visibility.Visible; ShowStorageError(ex); }
    }
    private bool IsAtEnd()
    { var scroll = FindVisual<ScrollViewer>(History); return scroll is null || scroll.ScrollableHeight - scroll.VerticalOffset < 1; }
    private void ScrollLatest() { if (History.Rows.Count > 0) History.ScrollIntoView(History.Rows[^1]); NewMessages.Visibility = Visibility.Collapsed; }
    private async void OlderMessagesClick(object sender, RoutedEventArgs e) => await LoadHistoryAsync(true, false);
    private void NewMessagesClick(object sender, RoutedEventArgs e) { ScrollLatest(); QueueViewportRead(); }
    private void HistoryScrolled(object sender, ScrollChangedEventArgs e) { if (_initializedInbox) ViewChanged(); }
    private void ViewChanged()
    { if (!_initializedInbox) return; Interlocked.Increment(ref _viewGeneration); _runtime?.Reads.Update(VisibleContext(_selected?.ConversationId)); UpdatePresenceView(); QueueViewportRead(); }
    private VisibleReadContext VisibleContext(string? id) => new(id, _userOpened && !_showInbox, IsActive && IsVisible && SettingsPane.Visibility != Visibility.Visible, WindowState == WindowState.Minimized, _historyLoaded && History.ActualHeight > 0);
    private void QueueViewportRead() { if (!_stopping && _initializedInbox) { _readTimer.Stop(); _readTimer.Start(); } }
    private async Task MarkViewportAsync()
    {
        if (_runtime is null || _selected is not { } entry || !_historyLoaded || _readBusy || !VisibleContext(entry.ConversationId).WindowActive || !_userOpened || _showInbox) return;
        _readBusy = true; long generation = Volatile.Read(ref _viewGeneration); var visible = new List<MessageView>();
        try
        {
            var viewport = new Rect(0, 0, History.ActualWidth, History.ActualHeight);
            foreach (var row in History.Rows)
            {
                if (History.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container || container.ActualHeight <= 0) continue;
                var bounds = container.TransformToAncestor(History).TransformBounds(new Rect(container.RenderSize)); var intersection = Rect.Intersect(viewport, bounds);
                if (!intersection.IsEmpty && ReadVisibility.IsVisible(bounds.Height, intersection.Height, 18, viewport.Height)) visible.Add(row);
            }
            var signed = visible.Where(r => !r.Legacy && !r.Outgoing && r.Sequence > 0).Select(r => r.Sequence).Take(200).ToArray();
            var legacy = visible.Where(r => r.Legacy && r.Sequence > 0).Select(r => r.Sequence).Take(200).ToArray();
            var context = VisibleContext(entry.ConversationId); long readGeneration = _runtime.Reads.Update(context);
            bool StillCurrent() => !_stopping && generation == Volatile.Read(ref _viewGeneration);
            int marked = 0;
            if (entry.ConversationId is { } conv && signed.Length > 0)
            {
                if (!_runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(conv, out var principal))
                    marked += (await _runtime.Worker.Run(s => StillCurrent() ? s.MarkClassroomVisibleRead(principal, signed, context, StillCurrent) : new VisibleReadResult(0, 0, "stale-view"))).Marked;
                else
                    marked += (await _runtime.Worker.Run(_ => StillCurrent() ? _runtime.Reads.Mark(conv, readGeneration, signed) : new(0, 0, "stale-view"))).Marked;
            }
            if (entry.LegacyScope is { } scope && legacy.Length > 0) marked += (await _runtime.Worker.Run(s => s.MarkLegacyVisibleRead(scope, legacy, context with { ActiveConversationId = scope.ContextId }, StillCurrent))).Marked;
            if (marked > 0) { _runtime.Receipts?.Pulse(); _runtime.ClassroomReceipts?.Pulse(); await RefreshInboxAsync(); }
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { _readBusy = false; }
    }
    private void UpdatePresenceView()
    {
        if (_runtime?.Presence is not { } service) return;
        var viewport = new Rect(0, 0, InboxList.ActualWidth, InboxList.ActualHeight);
        var rows = _inbox.Where(r => r.Entry.RosterId is not null && InboxList.ItemContainerGenerator.ContainerFromItem(r) is FrameworkElement c && c.IsVisible &&
            Rect.Intersect(viewport, c.TransformToAncestor(InboxList).TransformBounds(new Rect(c.RenderSize))) is { IsEmpty: false, Height: > 0 }).Select(r => r.Entry.RosterId!).ToArray();
        service.SetView(IsVisible && WindowState != WindowState.Minimized && SettingsPane.Visibility != Visibility.Visible, rows, _selected?.RosterId);
    }
    private void ShellSizeChanged(object sender, SizeChangedEventArgs e) { if (_initializedInbox) { UpdateShellLayout(); ViewChanged(); } }
    private void UpdateShellLayout()
    {
        if (!_initializedInbox) return; bool narrow = Shell.ActualWidth > 0 && Shell.ActualWidth <= 700;
        bool inbox = narrow && _showInbox; InboxColumn.Width = new(narrow ? 0 : 288);
        InboxPane.Visibility = !narrow || inbox ? Visibility.Visible : Visibility.Collapsed; ConversationPane.Visibility = inbox ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(InboxPane, inbox ? 2 : 1); BackToInbox.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        bool compact = Shell.ActualHeight > 0 && Shell.ActualHeight < 430;
        Draft.Height = compact ? 55 : 72; Grid.SetColumnSpan(Draft, compact ? 1 : 2); Grid.SetRowSpan(Draft, compact ? 2 : 1);
        Grid.SetRow(SendButton, compact ? 0 : 1); Grid.SetRowSpan(SendButton, compact ? 2 : 1); SendButton.MinHeight = compact ? 55 : 40; SendButton.Margin = compact ? new(8, 0, 0, 0) : new(8, 8, 0, 0);
        CodeToolbar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; CodeToolbarRow.Height = compact ? new(0) : GridLength.Auto;
        ComposerToolsScroll.MaxHeight = Math.Min(170, Math.Max(0, Shell.ActualHeight - 80 - (compact ? 140 : 210) - 40));
        CompactActions.Visibility = compact || ComposerToolsScroll.MaxHeight < 40 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BackToInboxClick(object sender, RoutedEventArgs e) { _showInbox = true; _userOpened = false; UpdateShellLayout(); ViewChanged(); }
    private void CompactActionsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var reaction in ClassroomReactions.All) { var item = new MenuItem { Header = reaction.Text + (reaction.TeacherOnly ? " → 老師" : " → 目前對象"), Tag = reaction.Id, IsEnabled = CanSendReaction }; item.Click += ReactionSendClick; menu.Items.Add(item); }
        menu.Items.Add(new Separator());
        foreach (var language in CodeLanguage.Items.OfType<ComboBoxItem>()) { var item = new MenuItem { Header = "插入 " + language.Content + " 程式碼", IsEnabled = Draft.IsEnabled }; item.Click += (_, _) => { CodeLanguage.SelectedItem = language; InsertCodeClick(sender, e); }; menu.Items.Add(item); }
        menu.PlacementTarget = CompactActions; menu.IsOpen = true;
    }
    internal void ShowConversationForLayoutCheck() { _showInbox = false; UpdateShellLayout(); }
    internal void ShowInboxForLayoutCheck() { _showInbox = true; UpdateShellLayout(); }
    internal void ShowSettingsForLayoutCheck(bool show) { SettingsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed; ViewChanged(); }
    private async void OpenSettingsClick(object sender, RoutedEventArgs e) { SettingsPane.Visibility = Visibility.Visible; ViewChanged(); await LoadManualFormAsync(_selected?.RosterId); }
    private void CloseSettingsClick(object sender, RoutedEventArgs e) { ++_manualFormGeneration; SettingsPane.Visibility = Visibility.Collapsed; ViewChanged(); }
    private async void AddRosterClick(object sender, RoutedEventArgs e) { SettingsPane.Visibility = Visibility.Visible; ViewChanged(); await LoadManualFormAsync(null); ContactName.Focus(); }
    private async Task LoadManualFormAsync(string? rosterId)
    {
        SaveContactFeedback.Text = ""; _editingRosterId = rosterId; _manualFormSnapshot = null; _pairingSelection = null; RefreshPairingControls(); long generation = ++_manualFormGeneration; SaveContactButton.IsEnabled = DeleteContactButton.IsEnabled = false;
        ManualRosterTitle.Text = rosterId is null ? "手動新增同學" : "編輯目前名單";
        SaveContactButton.Content = rosterId is null ? "新增到名單" : "儲存這位同學";
        if (_runtime is null) { ContactName.Text = StudentNumberBox.Text = RecipientIp.Text = ""; TeacherLabel.IsChecked = false; return; }
        try
        {
            var loaded = await _runtime.Worker.Run(s => (Rows: s.GetRosterSnapshot(), Target: rosterId is null ? null : s.CapturePairingRosterTarget(rosterId)));
            var rows = loaded.Rows;
            if (generation != _manualFormGeneration) return;
            var row = rosterId is null ? null : rows.FirstOrDefault(r => r.Entry.Id == rosterId);
            if (rosterId is not null && row is null) { StorageStatus.Text = "這筆名單已移除，請重新開啟設定。"; return; }
            _manualFormSnapshot = RosterImportPreview.ComputeSnapshotToken(rows); ContactName.Text = row?.Entry.DisplayName ?? ""; StudentNumberBox.Text = row?.Entry.StudentNumber ?? ""; RecipientIp.Text = row?.Entry.Endpoint ?? ""; TeacherLabel.IsChecked = row?.Entry.IsTeacher == true;
            SaveContactButton.IsEnabled = true; DeleteContactButton.IsEnabled = row is not null; _pairingSelection = loaded.Target; RefreshPairingControls();
        }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private async Task SaveRosterAsync(string name, string ip)
    {
        if (_runtime is null) return;
        if (_manualFormSnapshot is not { } token) { SetStatus("請重新開啟名單設定後再儲存。"); return; }
        string? id = _editingRosterId; long generation = _manualFormGeneration;
        try
        {
            var edit = new RosterEdit(name, string.IsNullOrWhiteSpace(StudentNumberBox.Text) ? null : StudentNumberBox.Text.Trim(), TeacherLabel.IsChecked == true, ip.Length == 0 ? null : ip);
            await _runtime.InvalidatePairingAsync("roster_edit");
            var result = await _runtime.Worker.Run(s => s.SaveManualRosterEntry(id, edit, token));
            if (result.Changed || result.Reason == "unchanged")
            {
                if (generation == _manualFormGeneration) { _editingRosterId = result.EntryId; _manualFormSnapshot = result.SnapshotToken; SaveContactButton.Content = "儲存這位同學"; DeleteContactButton.IsEnabled = true; SaveContactFeedback.Foreground = System.Windows.Media.Brushes.DarkGreen; SaveContactFeedback.Text = "✅ 名單已儲存"; }
                if (generation == _manualFormGeneration) await LoadManualFormAsync(result.EntryId);
                SetStatus(result.Changed ? "名單已儲存。" : "名單沒有變動。"); _runtime.Presence?.Refresh(); ResetRecipientProtocolHints(); await RefreshInboxAsync();
                if (id is null)
                {
                    SettingsPane.Visibility = Visibility.Collapsed;
                    ViewChanged();
                    SelectEndpoint(ip.Length > 0 ? ip : null);
                }
            }
            else
            {
                SaveContactFeedback.Foreground = System.Windows.Media.Brushes.Firebrick;
                SaveContactFeedback.Text = "❌ " + RosterFailure(result.Reason);
                StorageStatus.Text = RosterFailure(result.Reason);
            }
        }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private async void DeleteRosterClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _editingRosterId is not { } id) return;
        try
        {
            var preview = await _runtime.Worker.Run(s => s.PreviewRosterDelete(id));
            if (!preview.CanDelete) { StorageStatus.Text = RosterFailure(preview.Reason); return; }
            if (MessageBox.Show(this, preview.Notice, "移除名單標籤", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await _runtime.InvalidatePairingAsync("roster_delete");
            var result = await _runtime.Worker.Run(s => s.DeleteRosterEntry(id, preview.SnapshotToken, preview.ToConfirmation()));
            if (!result.Changed) { StorageStatus.Text = RosterFailure(result.Reason); return; }
            await LoadManualFormAsync(null); _runtime.Presence?.Refresh(); ResetRecipientProtocolHints(); await RefreshInboxAsync(true); SetStatus("名單標籤已移除，對話仍保留。");
        }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private static string RosterFailure(string reason) => reason switch {
        "roster_changed" => "名單已變動，尚未儲存。請保留目前輸入，重新開啟設定後再檢查。",
        "confirmation_mismatch" => "名單已被修改，請重新開啟設定後再確認刪除。",
        "not-found" => "這筆名單已不存在。",
        "duplicate-endpoint" => "這個 IP 已在名單中，請編輯原本那位同學。",
        "duplicate-student-number" => "學號已存在，請檢查原本的名單。",
        "invalid-endpoint" => "請填寫可用的同網段 IPv4；不能填自己、廣播或回送位址。",
        "invalid-student-number" => "學號需為 1 到 12 位數字，或留白。",
        "invalid-name" => "姓名需為 1 到 64 字且不含控制字元。",
        _ => "無法儲存名單，請重新讀取後再試。" };
    private async void ReadReceiptToggleClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null) { ReadReceiptToggle.IsChecked = false; return; }
        bool enabled = ReadReceiptToggle.IsChecked == true; long edit = ++_readPolicyEdit;
        try { var result = await _runtime.Worker.Run(s => s.SetReadReceiptPolicy(enabled)); if (edit == _readPolicyEdit) ReadReceiptToggle.IsChecked = result.Policy.Enabled; _runtime.Receipts?.Pulse(); _runtime.ClassroomReceipts?.Pulse(); SetStatus(result.Policy.Enabled ? "對外已讀回條已開啟；不補送先前已讀。" : $"對外已讀回條已關閉，移除 {result.DroppedReceipts} 筆未送回條。"); }
        catch (Exception ex) { ReadReceiptToggle.IsEnabled = false; ShowStorageError(ex); }
    }
    private void InsertCodeClick(object sender, RoutedEventArgs e)
    {
        if (!Draft.IsEnabled) return; string language = (CodeLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "text";
        int start = Draft.SelectionStart; string selection = Draft.SelectedText;
        Draft.SelectedText = "\n```" + language + "\n" + selection + "\n```\n"; Draft.Select(start + language.Length + 5, selection.Length); Draft.Focus();
    }
    private void ReactionPreviewToggleClick(object sender, RoutedEventArgs e)
    { if (UpdateSettings(settings with { ShowReactionPreviews = ReactionPreviewToggle.IsChecked == true })) SetStatus(ReactionPreviewToggle.IsChecked == true ? "已允許課堂反應圖片；一般訊息提醒仍不顯示正文。" : "桌面提醒僅顯示姓名與新訊息。"); }
    private async void ReloadDataClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null) { DataRetryRequested?.Invoke(); return; }
        try { long edit = ++_readPolicyEdit; var policy = await _runtime.Worker.Run(s => s.GetReadReceiptPolicy()); if (edit == _readPolicyEdit) { ReadReceiptToggle.IsChecked = policy.Enabled; ReadReceiptToggle.IsEnabled = true; } await FlushDraftsAsync(); _historyLoaded = false; await RefreshInboxAsync(true); }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private void ShowStorageError(Exception exception)
    {
        if (_closingRequested && exception is OperationCanceledException) return;
        StorageStatus.Text = exception is StoreException store ? store.Message : exception is ArgumentException ? "資料格式不正確，請檢查輸入後重試。" : "本機資料無法讀取或儲存，請重試。";
        ReloadData.Visibility = Visibility.Visible;
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T found) return found; if (FindVisual<T>(child) is { } nested) return nested; } return null; }
    internal string LegacyNotificationName(NekoChat.Core.LegacyDeliveryNotice notice) => _snapshot?.Entries.FirstOrDefault(e => e.LegacyScope == notice.Scope)?.NotificationName ?? "未驗證來源";
    internal string PeerNotificationName(string peerId) => _snapshot?.Entries.FirstOrDefault(e => e.PeerId == peerId)?.NotificationName ?? "未命名同學";
    internal string ClassroomNotificationName(string sourceIp)
    {
        var entry = _snapshot?.Entries.FirstOrDefault(e => e.Endpoint == sourceIp);
        return ClassroomNotificationNames.Format(sourceIp, entry?.DisplayName, entry?.Removed == true);
    }
    internal string? PeerReplyEndpoint(string peerId) => _snapshot?.Entries.FirstOrDefault(e => e.PeerId == peerId)?.Endpoint;
    internal bool CanSendReaction => !_closingRequested && _receiverRunning && !_sending;
    internal void AbortClosing() { IsEnabled = true; _closingRequested = false; _stopping = false; UpdateComposer(); }
}
