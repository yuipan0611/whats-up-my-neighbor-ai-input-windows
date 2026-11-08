using System.Collections.ObjectModel;
using System.Diagnostics;
using NekoChat.Core.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NekoChat.Core;
using NekoChat.Core.Attachments;
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
    public ImageSource Avatar => CatArtwork.ResolvePeerAvatar(Entry.PeerId ?? Entry.RosterId, Entry.StudentNumber ?? Entry.DisplayName);
    public string Secondary => Entry.DisplayName != Entry.Endpoint ? Entry.Endpoint ?? "" : "";
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
    private bool _selectionClearedAfterRemoval;
    private TaskCompletionSource? _inboxRefreshCompleted;
    private long _viewGeneration;
    private long _readPolicyEdit;
    private long _manualFormGeneration;
    private string? _editingRosterId, _manualFormSnapshot;
    private long _selectionRequest;
    private bool _replyLookupPending;
    private long? _beforeMessage, _beforeLegacy, _beforePending;
    private IReadOnlyList<ClassroomPendingMessage> _pendingMessages = [];
    private string? _pendingConversation;
    private bool _historyLoaded, _historyAnchored;
    private int _peerPort = DirectLanMessaging.DefaultPort;
    internal event Action? DataRetryRequested;
    internal event Action? IdentityRecoveryRequested;
    private bool _identityRecoveryBusy;
    private void CheckIdentityClick(object sender, RoutedEventArgs e) { if (_runtime?.ArchivedIdentityFixture == true) IdentityRecoveryRequested?.Invoke(); }
    internal void SetIdentityRecoveryStatus(string text, bool busy)
    { _identityRecoveryBusy = busy; IdentityRecoveryStatus.Text = text; CheckIdentityButton.IsEnabled = !busy && _runtime is not null && !_closingRequested; }

    private void InitializeInbox()
    {
        InboxList.ItemsSource = _inbox; TeachersList.ItemTemplate = InboxList.ItemTemplate;
        foreach (var list in new[] { InboxList, TeachersList })
        {
            list.ContextMenu = new ContextMenu();
            list.ContextMenuOpening += NicknameMenuOpening;
        } _initializedInbox = true;
        InitializeAttachments(); InitializeUi4();
        InboxList.PreviewMouseLeftButtonUp += async (_, e) => {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(InboxList, source) is ListBoxItem { DataContext: InboxRowView row } &&
                (!_userOpened || _showInbox || _selected?.Id != row.Entry.Id)) await SelectEntryAsync(row.Entry, true);
        };
        TeachersList.PreviewMouseLeftButtonUp += async (_, e) => {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(TeachersList, source) is ListBoxItem { DataContext: InboxRowView row } &&
                (!_userOpened || _showInbox || _selected?.Id != row.Entry.Id)) await SelectEntryAsync(row.Entry, true);
        };
        TeachersList.PreviewKeyDown += async (_, e) => {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && !e.IsRepeat && TeachersList.SelectedItem is InboxRowView row)
            { e.Handled = true; await SelectEntryAsync(row.Entry, true); }
        };
        InboxList.PreviewKeyDown += async (_, e) => {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && !e.IsRepeat && InboxList.SelectedItem is InboxRowView row)
            { e.Handled = true; await SelectEntryAsync(row.Entry, true); }
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await FlushDraftsAsync(); };
        _readTimer.Tick += async (_, _) => { _readTimer.Stop(); await MarkViewportAsync(); };
        Activated += (_, _) => ViewChanged(); Deactivated += (_, _) => ViewChanged(); StateChanged += (_, _) => ViewChanged(); IsVisibleChanged += async (_, _) => { ViewChanged(); await ShowReadReceiptNoticeAsync(); };
        History.PreviewMouseDown += (_, _) => { if (_selected is not null) { _userOpened = true; _showInbox = false; ViewChanged(); } };
        UpdateComposer(); UpdateShellLayout();
    }
    internal async Task AttachRuntimeAsync(ChatRuntime runtime, int peerPort)
    {
        _diagnosticFailures = []; _runtime = runtime; SetDiagnosticsDirectory(Path.Combine(Path.GetDirectoryName(runtime.DataDirectory)!, "diagnostics")); ResetRecipientProtocolHints(); ArchivedIdentityPanel.Visibility = runtime.ArchivedIdentityFixture ? Visibility.Visible : Visibility.Collapsed; _peerPort = peerPort; IdentityStatus.Text = runtime.IdentityStatus; StorageStatus.Text = runtime.StartupWarning ?? "";
        var policy = await runtime.Worker.Run(s => s.GetReadReceiptPolicy());
        ReadReceiptToggle.IsChecked = policy.Enabled;
        if (runtime.Presence is { } presence) presence.StateChanged += PresenceChanged;
        await BootstrapClassRosterAsync();
        await SyncTeacherProjectionAsync();
        await RefreshInboxAsync();
        await ShowReadReceiptNoticeAsync();
    }
    internal void ShowDataFailure(Exception error) => ShowDataFailure(DiagnosticMessage(DiagnosticFault.FromException(error)));
    internal void ShowDataFailure(string message)
    { StorageStatus.Text = message; ReloadData.Visibility = Visibility.Visible; UpdateComposer(); }
    internal void DetachRuntime() { ++_teacherFilesRequest; _pairingWindow?.CloseSafely(); _pairingSelection = null; if (_runtime?.Presence is { } presence) presence.StateChanged -= PresenceChanged; _runtime = null; ResetRecipientProtocolHints(); ArchivedIdentityPanel.Visibility = Visibility.Collapsed; _receiverRunning = false; ReceiverStatus.Text = "接收暫停，等待本機資料就緒。"; RefreshPairingControls(); UpdateComposer(); }
    internal void DataChanged() { if (!_stopping) _ = RefreshInboxAsync(refreshHistory: true); }
    private void PresenceChanged() => Dispatcher.BeginInvoke(new Action(() => { if (!_stopping) { ApplyInboxFilter(); UpdateHeader(); } }));
    private async Task RefreshInboxAsync(bool refreshHistory = false, bool waitForCompletion = false)
    {
        if (_runtime is null || _stopping) return;
        if (_refreshing)
        {
            _refreshAgain = true;
            if (waitForCompletion && _inboxRefreshCompleted is { } pending) await pending.Task;
            return;
        }
        if (_diagnosticFailures.Count > 0 || _runtime.OutboxFailure is not null || StorageStatus.Text.StartsWith("待送儲存暫停：", StringComparison.Ordinal)) RefreshDiagnosticStatus();
        _refreshing = true;
        var completed = _inboxRefreshCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long diagnosticStart = Stopwatch.GetTimestamp();
        _runtime.Diagnostics.Record(DiagnosticStage.UiRefresh, DiagnosticOutcome.Started);
        try
        {
            do
            {
                _refreshAgain = false;
                ContactsSmokeBeforeInboxRead();
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
                _selected = current is null ? _replyLookupPending || _selectionClearedAfterRemoval ? null : _snapshot.Entries.FirstOrDefault() : _snapshot.Entries.FirstOrDefault(e => e.Id == current);
                bool changed = previous?.Id != _selected?.Id || previous?.ConversationId != _selected?.ConversationId;
                if (changed) { _userOpened = false; _historyLoaded = false; History.Rows.Clear(); SetDraftText(CurrentDraft()?.Text ?? ""); ViewChanged(); }
                ApplyInboxFilter();
                if (_selected is not null)
                {
                    EnsureDraft(_selected); if (changed) SetDraftText(CurrentDraft()?.Text ?? ""); UpdateHeader();
                    if (!_showTeacherFiles && (!_historyLoaded || refreshHistory)) await LoadHistoryAsync(older: false, reset: !_historyLoaded);
                }
                else { History.Rows.Clear(); OlderMessages.Visibility = Visibility.Collapsed; HistoryState.Text = "選擇一位同學，或新增名單。"; HistoryState.Visibility = Visibility.Visible; UpdateHeader(); }
                if (_showTeacherFiles) await LoadTeacherFilesAsync(false);
                UpdateComposer();
            } while (_refreshAgain && !_stopping);
            ClearRecoveredDiagnostic(DiagnosticStage.UiRefresh);
            _runtime.Diagnostics.Record(DiagnosticStage.UiRefresh, DiagnosticOutcome.Succeeded, count: _snapshot?.Entries.Count ?? 0, elapsedMs: (long)Stopwatch.GetElapsedTime(diagnosticStart).TotalMilliseconds);
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { _refreshing = false; completed.TrySetResult(); }
    }
    private string StateOf(InboxEntry entry)
    {
        if (_runtime?.ArchivedIdentityFixture != true)
        {
            if (entry.Blocked) return "已封鎖，歷史唯讀";
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
        string query = InboxSearch.Text.Trim(); bool unread = InboxFilter.SelectedIndex == 1; int section=InboxFilter.SelectedIndex;
        var entries = _snapshot.Entries.Where(e => (section==2 ? e.Removed&&!e.Blocked : section==3 ? e.Blocked : !e.IsTeacher&&!e.Removed&&!e.Blocked) && (!unread || e.TotalUnread > 0) && (query.Length == 0 || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.StudentNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) == true || e.Endpoint?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).OrderBy(e => EndpointSortKey(e.Endpoint));
        _changingSelection = true;
        try
        {
            TeachersList.ItemsSource = _snapshot.Entries.Where(e => e.IsTeacher&&!e.Removed&&!e.Blocked).Select(e => new InboxRowView(e, StateOf(e))).ToArray();
            TeachersList.SelectedItem = _showTeacherFiles ? null : TeachersList.Items.OfType<InboxRowView>().FirstOrDefault(e => e.Entry.Id == _selected?.Id);
            SetTeacherButton.Visibility = _snapshot.Entries.Any(e => e.IsTeacher && !e.Removed&&!e.Blocked) ? Visibility.Collapsed : Visibility.Visible;
            _inbox.Clear(); foreach (var entry in entries) _inbox.Add(new(entry, StateOf(entry)));
            InboxList.SelectedItem = _showTeacherFiles ? null : _inbox.FirstOrDefault(e => e.Entry.Id == _selected?.Id);
        }
        finally { _changingSelection = false; }
        InboxCount.Text = $"{(section==2 ? "封存" : section==3 ? "已封鎖" : "同學")} · {_inbox.Count} 位";
        bool noEntries = !_snapshot.Entries.Any(e => !e.IsTeacher&&!e.Removed&&!e.Blocked);
        InboxEmpty.Text = noEntries ? "尚無同學對話。\n新增名單後，即可開始一對一聊天。" : "沒有符合搜尋或篩選的同學。";
        if(section>=2) InboxEmpty.Text=section==2 ? "沒有封存的對話。" : "沒有已封鎖的聯絡人。";
        AddFirstContactButton.Visibility = noEntries && section<2 ? Visibility.Visible : Visibility.Collapsed;
        InboxEmptyPanel.Visibility = _inbox.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdatePresenceView();
    }
    private static uint EndpointSortKey(string? endpoint)
    {
        if (!System.Net.IPAddress.TryParse(endpoint, out var ip) || ip.GetAddressBytes() is not { Length: 4 } bytes) return uint.MaxValue;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
    private void InboxSearchChanged(object sender, TextChangedEventArgs e) { if (_initializedInbox) InboxSearchHint.Visibility = string.IsNullOrEmpty(InboxSearch.Text) ? Visibility.Visible : Visibility.Collapsed; ApplyInboxFilter(); }
    private void InboxFilterChanged(object sender, SelectionChangedEventArgs e) { if (_initializedInbox) ApplyInboxFilter(); }
    private async void InboxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || InboxList.SelectedItem is not InboxRowView row) return;
        await SelectEntryAsync(row.Entry, userOpened: true);
    }
    private async Task SelectEntryAsync(InboxEntry entry, bool userOpened, long? focusSequence = null)
    {
        _selectionClearedAfterRemoval = false; _showRemovalNotice = false;
        ++_selectionRequest; ++_teacherFilesRequest; _showTeacherFiles = false; History.Visibility = Visibility.Visible; _replyLookupPending = false;
        _selected = entry; _userOpened = userOpened; _showInbox = false; _historyLoaded = false; _beforeMessage = _beforeLegacy = _beforePending = null;
        ApplyInboxFilter(); ViewChanged(); History.Rows.Clear(); EnsureDraft(entry); SetDraftText(CurrentDraft()?.Text ?? "");
        RecipientIp.Text = entry.Endpoint ?? ""; UpdateHeader(); UpdateShellLayout(); UpdateComposer(); await LoadHistoryAsync(false, true, focusSequence);
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
            var snapshot = await _runtime.Worker.Run(s => _runtime.ArchivedIdentityFixture ? s.GetInboxSnapshot() : s.GetClassroomInboxSnapshot().ToInboxSnapshot()); if (request != _selectionRequest) return;
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
        _ = RefreshAttachmentDraftAsync();
    }
    private DraftEditBuffer? CurrentDraft() => _selected?.ConversationId is { } id && _drafts.TryGetValue(id, out var buffer) ? buffer : null;
    private void SetDraftText(string text) { _settingDraft = true; try { Draft.Text = _selected is { Removed: true } or { Blocked: true } ? "" : text; } finally { _settingDraft = false; } }
    private void DraftEdited(object sender, TextChangedEventArgs e)
    {
        if (!_initializedInbox || _settingDraft || _selected is { Removed: true } or { Blocked: true }) return;
        if (CurrentDraft() is { Clearing: false } buffer) { try { buffer.Edit(Draft.Text); _saveTimer.Stop(); _saveTimer.Start(); } catch (Exception ex) { ShowStorageError(ex); } }
        UpdateComposer();
    }
    private readonly SemaphoreSlim _draftSaveGate = new(1, 1);
    internal async Task FlushDraftsAsync()
    {
        await _draftSaveGate.WaitAsync();
        try
        {
            if (_runtime is not { } runtime) return;
            foreach (var pair in _drafts.Where(p => p.Value.Dirty && !p.Value.Clearing).ToArray())
            {
                var ticket = pair.Value.Capture();
                try
                {
                    var result = await runtime.Worker.Run(s => {
                        if (!runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(pair.Key, out var principal))
                        {
                            var cr = s.SaveClassroomDraft(principal, ticket.Text, ticket.Epoch, ticket.Revision);
                            return new DraftSaveResult(cr.Saved, cr.Reason, cr.CurrentEpoch, cr.CurrentRevision);
                        }
                        return ticket.Persist(s, pair.Key);
                    });
                    if (SmokeBeforeDraftWriteCompletion is { } beforeCompletion) await beforeCompletion();
                    pair.Value.CompleteWrite(ticket, result);
                    if (!result.Saved) runtime.Diagnostics.Record(DiagnosticStage.StorageWrite, DiagnosticOutcome.Rejected, DiagnosticFault.FromReason(result.Reason).Code);
                    if (!result.Saved && pair.Value.IsCurrent(ticket) && pair.Value.Dirty)
                        SetStorageNotice(result.Reason == "draft-too-large" ? "草稿太長無法保存，請縮短。" : "草稿尚未儲存，請重新讀取或保留目前文字後重試。");
                }
                catch (Exception ex) { ShowStorageError(ex); }
            }
            UpdateComposer();
        }
        finally { _draftSaveGate.Release(); }
    }
    private void UpdateComposer()
    {
        if (!_initializedInbox) return;
        var buffer = CurrentDraft(); bool attached = _runtime is not null; bool ready = attached && _runtime!.CanSend && (_runtime.ArchivedIdentityFixture ? _selected?.Pairing == PairingState.Paired : _selected?.CanSend == true);
        bool isRemoved = _selected?.Removed == true || _selected?.Blocked == true;
        // Hide the projection only: never edit/clear the retained draft buffer or its SQLite row.
        if (isRemoved && Draft.Text.Length > 0) SetDraftText("");
        else if (!isRemoved && Draft.IsReadOnly) SetDraftText(buffer?.Text ?? "");
        Draft.IsReadOnly = isRemoved;
        Draft.IsEnabled = !_showTeacherFiles && !_stopping && !_closingRequested && (attached ? buffer is { Clearing: false } : _receiverRunning) && !isRemoved;
        bool hasAttachment = _attachmentDraft is not null;
        bool valid = hasAttachment ? Encoding.UTF8.GetByteCount(Draft.Text) <= (_runtime?.MaximumTextBytes ?? 65536) : !string.IsNullOrWhiteSpace(Draft.Text) && (_runtime?.MaximumTextBytes is not { } limit || V2Text.Check(Draft.Text, limit) == V2TextProblem.None);
        AttachFileButton.IsEnabled = Draft.IsEnabled && !_sending && !_attachmentDraftLoading && !_attachmentBusy && _runtime?.Attachments is not null;
        InsertCodeButton.IsEnabled = Draft.IsEnabled && !isRemoved;
        CodeLanguage.IsEnabled = InsertCodeButton.IsEnabled;
        AttachFileButton.ToolTip = isRemoved ? "此對話為唯讀，無法附加檔案或相片。" : "附加檔案或相片 · 每則 1 個，最多 25 MiB";
        InsertCodeButton.ToolTip = isRemoved ? "此對話為唯讀，無法插入程式碼。" : "插入程式碼";
        ToolTipService.SetShowOnDisabled(AttachFileButton, true); ToolTipService.SetShowOnDisabled(InsertCodeButton, true);
        RemoveAttachmentButton.IsEnabled = !_sending && !_attachmentBusy;
        SendButton.IsEnabled = !_attachmentDraftLoading && (!hasAttachment || _runtime?.Attachments is not null) &&
            !_attachmentBusy && !_closingRequested && !_sending && _activeSend.IsCompleted && _receiverRunning && valid && (attached ? ready && buffer is { Clearing: false } : true);
        ClearAttachmentCacheButton.IsEnabled = _runtime?.Attachments is not null;
        bool teacher = !_showTeacherFiles && _selected?.IsTeacher == true && !_selected.Removed;
        TeacherReactions.Visibility = teacher ? Visibility.Visible : Visibility.Collapsed;
        PeerReactions.Visibility = Visibility.Collapsed;
        StickerButton.Visibility = !_showTeacherFiles && _selected is not null && !teacher ? Visibility.Visible : Visibility.Collapsed;
        StickerButton.IsEnabled = Draft.IsEnabled && !isRemoved && CanSendReaction && _selected?.CanSend == true;
        StickerButton.ToolTip = isRemoved ? "此對話為唯讀，無法傳送貼圖。" : "傳送貼圖"; ToolTipService.SetShowOnDisabled(StickerButton, true);
        TeacherReactions.IsEnabled = ScrollReaction.IsEnabled = CanSendReaction && _selected?.CanSend == true && !isRemoved;
        var attempt = _selected?.Attempt; AttemptBanner.Visibility = Visibility.Collapsed;
        bool cancelling = attempt is not null && _runtime?.IsAttemptCancellationRequested(attempt.MessageId, attempt.TryCount) == true;
        AttemptText.Text = ClassroomSendUiPolicy.DecideAttemptBanner(attempt?.State, cancelling);
        RetryAttempt.IsEnabled = ready && !_sending && !_attachmentDraftLoading && !hasAttachment && attempt is { State: not "sending" } && attempt.RawSnapshot == Draft.Text;
        CancelAttemptButton.IsEnabled = !_closingRequested && attached && !cancelling && attempt is { State: not ("cancelled" or "confirmed" or "superseded") } &&
            (attempt.State != "sending" || _runtime!.CanCancelAttempt(attempt.MessageId, attempt.TryCount));
        // A local stop remains useful for pre-existing attempts, even in a read-only archive.
        CancelAttemptButton.ToolTip = "停止本機後續重試，不會向對方傳送訊息，也不會收回已送出的訊息。";
        ClearConversationButton.IsEnabled = attached && _selected is not null && buffer?.Clearing != true;
        CheckIdentityButton.IsEnabled = _runtime?.ArchivedIdentityFixture == true && !_identityRecoveryBusy && !_closingRequested;
        int byteCount = Encoding.UTF8.GetByteCount(Draft.Text);
        int maxBytes = _runtime?.MaximumTextBytes ?? 65536;
        string count = byteCount > 55000 ? $"{byteCount} / {maxBytes} UTF-8 位元組 · " : "";
        string? readOnlyPrompt = _selected?.Blocked == true ? "此對象已封鎖，僅供查看歷史；草稿已保留。" :
            _selected?.Removed == true ? "這是封存對話，僅供查看歷史；草稿已保留。" : null;
        string prompt = readOnlyPrompt ?? (!attached ? "Enter 傳送 · Shift＋Enter 換行" :
            buffer is null ? "選擇一位同學後開始聊天" :
            _selected?.Endpoint is null ? "請從對話選單編輯對方 IP 位址" :
            !_runtime!.CanSend ? "教室傳送服務尚未就緒…" :
            !valid && Draft.Text.Length > 0 ? "⚠️ 訊息容量超過上限" :
            "Enter 傳送 · Shift＋Enter 換行");
        DraftStatus.Text = count + prompt;
        DraftPlaceholder.Text = readOnlyPrompt ?? (_selected is null ? "選擇對象後輸入訊息…" : "傳訊息給" + _selected.DisplayName + "…");
        DraftPlaceholder.Visibility = Draft.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncAttemptMessage(); Status.Visibility = attempt is not null && !_showRemovalNotice ? Visibility.Collapsed : Visibility.Visible; UpdateShellLayout();
    }
    private void UpdateHeader()
    {
        if (_showTeacherFiles) { ConversationTitle.Text = "課堂檔案"; ConversationMeta.Text = "全班共用 · 老師與同學都能分享"; TrustBanner.Visibility = Visibility.Collapsed; return; }
        ConversationTitle.Text = _selected?.DisplayName ?? "選擇一位同學";
        ConversationMeta.Text = _selected is { } entry ? entry.Removed || entry.Blocked || entry.Endpoint is null ? StateOf(entry) :
            entry.DisplayName == entry.Endpoint ? (entry.IsTeacher ? "老師" : "同學") + " · 一對一聊天" : entry.Endpoint : "對話紀錄保存在本機";
        bool legacy = CurrentRecipientIsLegacy;
        TrustBanner.Visibility = legacy || History.Rows.Any(r => r.Legacy) ? Visibility.Visible : Visibility.Collapsed;
        TrustBannerText.Text = legacy ? MessageLimitPolicy.LegacyTooLargeNotice + "。傳送前會重新確認版本；舊版確認不代表已儲存或已讀。" :
            _runtime?.ArchivedIdentityFixture == true ? "含舊版・未驗證訊息；位址或名單名稱不代表身分。" : "含舊版訊息；舊版確認不代表對方已儲存或已讀。";
    }
    private async Task LoadHistoryAsync(bool older, bool reset, long? focusSequence = null)
    {
        if (_runtime is null || _selected is not { } entry) return;
        if (reset) { HistoryState.Text = "載入對話中…"; HistoryState.Visibility = Visibility.Visible; }
        bool wasAtEnd = History.Items.Count == 0 || IsAtEnd();
        var oldScroll = FindVisual<ScrollViewer>(History); double oldOffset = oldScroll?.VerticalOffset ?? 0, oldExtent = oldScroll?.ExtentHeight ?? 0; long selection = _selectionRequest;
        try
        {
            long? before = focusSequence is { } target ? checked(target + 1) : older ? _beforeMessage : null, legacyBefore = older ? _beforeLegacy : null;
            long? pendingBefore = older ? _beforePending : null;
            long? pendingThrough = !older && !reset && _pendingConversation == entry.ConversationId && _pendingMessages.Count > 0 ? _pendingMessages.Min(p => p.Sequence) : null;
            var page = await _runtime.Worker.Run(s => {
                if (!_runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal))
                {
                    var crPage = (!older || before is not null) ? s.ListClassroomMessages(principal, before) : null;
                    var legPage = entry.LegacyScope is { } scope && (!older || legacyBefore is not null) ? s.ListLegacyMessages(scope, legacyBefore) : null;
                    var pending = new List<ClassroomPendingMessage>(); long? cursor = pendingBefore, next = null;
                    if (!older || pendingBefore is not null)
                    {
                        do
                        {
                            var batch = s.ListClassroomPendingMessages(principal, 200, cursor);
                            pending.AddRange(batch); next = batch.Count == 200 ? batch[0].Sequence : null;
                            cursor = next;
                        } while (!older && pendingThrough is not null && next > pendingThrough);
                    }
                    return (Classroom: crPage, Message: (MessagePage?)null, Legacy: legPage, Pending: (IReadOnlyList<ClassroomPendingMessage>)pending, PendingBefore: next);
                }
                return (Classroom: (ClassroomMessagePage?)null,
                    Message: entry.ConversationId is { } conv && (!older || before is not null) ? s.ListMessages(conv, before) : null,
                    Legacy: entry.LegacyScope is { } legScope && (!older || legacyBefore is not null) ? s.ListLegacyMessages(legScope, legacyBefore) : null, Pending: (IReadOnlyList<ClassroomPendingMessage>)[], PendingBefore: (long?)null);
            });
            if (_stopping || selection != _selectionRequest || entry.Id != _selected?.Id) return;
            if (reset) { History.Rows.Clear(); _historyAnchored = focusSequence is not null; }
            _pendingMessages = older && _pendingConversation == entry.ConversationId ? _pendingMessages.Concat(page.Pending).DistinctBy(p => p.Attempt.MessageId).ToArray() : page.Pending;
            _pendingConversation = entry.ConversationId;
            var peerAvatar = CatArtwork.ResolvePeerAvatar(entry.PeerId ?? entry.RosterId, entry.StudentNumber ?? entry.DisplayName);
            var attachmentRows = _runtime.Attachments is { } attachments ? await attachments.HistoryAsync(entry.ConversationId ?? "") : [];
            if (selection != _selectionRequest || entry.Id != _selected?.Id) return;
            var crRows = page.Classroom?.Items.Select(m => new MessageView("cr:" + m.LocalSeq, m.RawText, m.Direction == "outgoing", false, m.LocalSeq, m.CreatedUtc,
                m.Direction == "outgoing" ? (m.DeliveryLabel ?? (m.RemoteReadAt is not null ? "已讀" : m.RemoteStoredAt is not null ? "已送出" : "")) : (m.LocalReadAt is not null ? "本機已讀" : ""), peerAvatar) { MessageId = m.MessageId }) ?? [];
            crRows = crRows.Select(row => { row.Attachment = attachmentRows.FirstOrDefault(a => a.LocalSeq == row.Sequence); if (row.Attachment is { } storedAttachment) row.Attachment = _runtime.AttachmentView(storedAttachment); if (row.Attachment is { } info) row.Delivery = info.StateText; row.AttachmentAction = AttachmentActionAsync; row.AttachmentPath = row.Attachment is { } a && _runtime.Attachments is { } service && service.Vault.Exists(a.File) ? service.Vault.PathFor(a.File.BlobId) : null; return row; });
            var rows = page.Message?.Items.Select(m => new MessageView("v2:" + m.LocalSeq, m.RawText, m.Direction == "outgoing", false, m.LocalSeq, m.CreatedUtc,
                m.Direction == "outgoing" ? m.RemoteReadAt is not null ? "已讀" : "對方已儲存" : m.LocalReadAt is not null ? "本機已讀" : "", peerAvatar)) ?? [];
            var legacy = page.Legacy?.Items.Select(m => new MessageView("v1:" + m.LegacySeq, m.RawText, false, true, m.LegacySeq, m.ReceivedUtc, LegacyIncomingLabel + (m.LocalReadAt is not null ? " · 本機已讀" : ""), peerAvatar)) ?? [];
            var existing = History.Rows.Where(x => x.AttemptId is null).ToDictionary(m => m.Key, StringComparer.Ordinal); int oldCount = existing.Count;
            foreach (var row in crRows.Concat(rows).Concat(legacy)) { row.ContactReadOnly=entry.Removed||entry.Blocked; if (!_runtime.ArchivedIdentityFixture && !row.Key.StartsWith("v2:", StringComparison.Ordinal)) { row.DeleteLocal = () => ConfirmDeleteLocalMessageAsync(entry, row); row.DeleteDisabledReason = () => LocalMessageDeleteReason(entry, row); } if (existing.TryGetValue(row.Key, out var old)) { old.Delivery = row.Delivery; old.AttachmentPath = row.AttachmentPath; old.Attachment = row.Attachment; old.ContactReadOnly=row.ContactReadOnly; old.RefreshActions(); } else existing.Add(row.Key, row); }
            var sorted = existing.Values.OrderBy(m => m.Utc, StringComparer.Ordinal).ThenBy(m => m.Legacy).ThenBy(m => m.Sequence).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                if (i < History.Rows.Count && ReferenceEquals(History.Rows[i], sorted[i])) continue;
                int index = History.Rows.IndexOf(sorted[i]); if (index >= 0) History.Rows.Move(index, i); else History.Rows.Insert(i, sorted[i]);
            }
            if (older || reset) { _beforeMessage = page.Classroom?.NextBeforeLocalSeq ?? page.Message?.NextBeforeLocalSeq; _beforeLegacy = page.Legacy?.NextBeforeLegacySeq; }
            _beforePending = page.PendingBefore;
            OlderMessages.Visibility = _beforeMessage is not null || _beforeLegacy is not null || _beforePending is not null ? Visibility.Visible : Visibility.Collapsed;
            _historyLoaded = true; HistoryState.Text = History.Rows.Count == 0 ? "還沒有訊息" : ""; HistoryState.Visibility = History.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncAttemptMessage();
            if (!older && (reset || wasAtEnd)) ScrollLatest(); else if (!older && History.Rows.Count > oldCount) NewMessages.Visibility = Visibility.Visible;
            if (older && oldScroll is not null) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if (selection == _selectionRequest && _selected?.Id == entry.Id) oldScroll.ScrollToVerticalOffset(oldOffset + Math.Max(0, oldScroll.ExtentHeight - oldExtent));
            }));
            SyncAttemptMessage();
            if (_historyAnchored) NewMessages.Visibility = Visibility.Visible;
            if (focusSequence is { } sequence && History.Rows.FirstOrDefault(x => x.Key == "cr:" + sequence) is { } focused)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (selection == _selectionRequest) History.ScrollIntoView(focused); }));
            UpdateHeader(); QueueViewportRead();
        }
        catch (Exception ex) { HistoryState.Text = "無法讀取對話，請重新讀取。"; HistoryState.Visibility = Visibility.Visible; ShowStorageError(ex); }
    }
    private bool IsAtEnd()
    { var scroll = FindVisual<ScrollViewer>(History); return scroll is null || scroll.ScrollableHeight - scroll.VerticalOffset < 1; }
    private void ScrollLatest() { if (History.Rows.Count > 0) History.ScrollIntoView(History.Rows[^1]); NewMessages.Visibility = Visibility.Collapsed; }
    private async void OlderMessagesClick(object sender, RoutedEventArgs e) => await LoadHistoryAsync(true, false);
    private async void NewMessagesClick(object sender, RoutedEventArgs e) { if (_historyAnchored) await LoadHistoryAsync(false, true); else ScrollLatest(); QueueViewportRead(); }
    private void HistoryScrolled(object sender, ScrollChangedEventArgs e) { if (_initializedInbox) ViewChanged(); }
    private void ViewChanged()
    { if (!_initializedInbox) return; Interlocked.Increment(ref _viewGeneration); _runtime?.Reads.Update(VisibleContext(_selected?.ConversationId)); UpdatePresenceView(); QueueViewportRead(); }
    private VisibleReadContext VisibleContext(string? id) => new(id, _userOpened && !_showInbox && !_showTeacherFiles, IsActive && IsVisible && SettingsPane.Visibility != Visibility.Visible, WindowState == WindowState.Minimized, _historyLoaded && History.ActualHeight > 0);
    private void QueueViewportRead() { if (!_stopping && _initializedInbox) { _readTimer.Stop(); _readTimer.Start(); } }
    private async Task MarkViewportAsync()
    {
        if (_runtime is null || _selected is not { } entry || !_historyLoaded || !VisibleContext(entry.ConversationId).WindowActive || !_userOpened || _showInbox || _showTeacherFiles) return;
        // No unread messages means there is no read position or receipt work to commit.
        if (!_runtime.ArchivedIdentityFixture && entry.TotalUnread == 0) return;
        // A viewport change can invalidate the in-flight read while SQLite is busy. Keep the latest view queued.
        if (_readBusy) { _runtime.Diagnostics.Record(DiagnosticStage.UiRead, DiagnosticOutcome.Deferred); QueueViewportRead(); return; }
        _readBusy = true; long generation = Volatile.Read(ref _viewGeneration); var visible = new List<MessageView>();
        try
        {
            var viewport = new Rect(0, 0, History.ActualWidth, History.ActualHeight);
            foreach (var row in History.Rows)
            {
                if (History.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container || container.ActualHeight <= 0) continue;
                var bounds = container.TransformToAncestor(History).TransformBounds(new Rect(container.RenderSize)); var intersection = Rect.Intersect(viewport, bounds);
                if (intersection.IsEmpty) continue;
                double visibleHeight = Math.Min(intersection.Height, Math.Min(bounds.Height, viewport.Height));
                if (ReadVisibility.IsVisible(bounds.Height, visibleHeight, 18, viewport.Height)) visible.Add(row);
            }
            _runtime.Diagnostics.Record(DiagnosticStage.UiRead, DiagnosticOutcome.Started, count: visible.Count);
            var signed = visible.Where(r => !r.Legacy && !r.Outgoing && r.Sequence > 0).Select(r => r.Sequence).Take(200).ToArray();
            var legacy = visible.Where(r => r.Legacy && r.Sequence > 0).Select(r => r.Sequence).Take(200).ToArray();
            var context = VisibleContext(entry.ConversationId); long readGeneration = _runtime.Reads.Update(context);
            bool StillCurrent() => !_stopping && generation == Volatile.Read(ref _viewGeneration);
            int marked = 0;
            if (!_runtime.ArchivedIdentityFixture && ClassroomPrincipalKey.TryParse(entry.ConversationId, out var classroom))
            {
                var anchor = visible.Where(row => row.AttemptId is null && row.Sequence > 0)
                    .OrderBy(row => row.Utc, StringComparer.Ordinal).ThenBy(row => row.Legacy).ThenBy(row => row.Sequence).LastOrDefault();
                if (anchor is not null)
                    marked = (await _runtime.ReadThroughAsync(classroom, entry.LegacyScope, anchor.Sequence, anchor.Legacy, context, StillCurrent)).Marked;
            }
            else
            {
                if (entry.ConversationId is { } conv && signed.Length > 0)
                    marked += (await _runtime.Worker.Run(_ => StillCurrent() ? _runtime.Reads.Mark(conv, readGeneration, signed) : new(0, 0, "stale-view"))).Marked;
                if (entry.LegacyScope is { } scope && legacy.Length > 0)
                    marked += (await _runtime.Worker.Run(s => s.MarkLegacyVisibleRead(scope, legacy, context with { ActiveConversationId = scope.ContextId }, StillCurrent))).Marked;
            }
            if (StillCurrent()) ClearRecoveredDiagnostic(DiagnosticStage.UiRead);
            _runtime.Diagnostics.Record(DiagnosticStage.UiRead, StillCurrent() ? DiagnosticOutcome.Succeeded : DiagnosticOutcome.Stale, StillCurrent() ? DiagnosticCode.None : DiagnosticCode.StaleView, count: marked);
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
        if (rows.Length == 0)
            rows = _inbox.Where(r => r.Entry.RosterId is not null).Select(r => r.Entry.RosterId!).ToArray();
        service.SetView(IsVisible && WindowState != WindowState.Minimized && SettingsPane.Visibility != Visibility.Visible, rows, _selected?.RosterId);
    }
    private void ShellSizeChanged(object sender, SizeChangedEventArgs e) { if (_initializedInbox) { UpdateShellLayout(); ViewChanged(); } }
    private void UpdateShellLayout()
    {
        if (!_initializedInbox) return;
        bool narrow = Shell.ActualWidth > 0 && Shell.ActualWidth <= 640;
        bool inbox = narrow && _showInbox; InboxColumn.Width = new(narrow ? 0 : 274);
        InboxPane.Visibility = !narrow || inbox ? Visibility.Visible : Visibility.Collapsed; ConversationPane.Visibility = inbox ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(InboxPane, inbox ? 2 : 1); BackToInbox.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        ChatHistoryPanel.Visibility = ComposerPanel.Visibility = _showTeacherFiles ? Visibility.Collapsed : Visibility.Visible;
        TeacherFilesPane.Visibility = _showTeacherFiles ? Visibility.Visible : Visibility.Collapsed;
        ConversationMenuButton.Visibility = _showTeacherFiles ? Visibility.Collapsed : Visibility.Visible;
        TeacherFilesButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_showTeacherFiles ? "#EDF2FF" : "#FAFBFD"));
        bool compact = Shell.ActualHeight > 0 && Shell.ActualHeight < 430;
        BrandHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TeacherSectionTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NicknameHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarFooter.Margin = compact ? new(16,4,16,4) : new(16,12,16,12);
        SidebarHeaderScroll.MaxHeight = Math.Max(150, Shell.ActualHeight - (compact ? 56 : 86) - 80);
        TeacherFilesButton.Padding = compact ? new(10,4,10,4) : new(12,8,12,8);
        TeacherFilesButton.Margin = compact ? new(0,4,0,8) : new(0,6,0,14);
        ComposerPanel.Padding = compact ? new(16, 0, 16, 0) : new(16, 12, 16, 12);
        Draft.Height = compact ? 55 : 72; Grid.SetColumnSpan(Draft, 2); Grid.SetRowSpan(Draft, 1);
        Grid.SetRow(SendButton, 2); Grid.SetRowSpan(SendButton, 1); SendButton.MinHeight = 40; SendButton.Margin = new(8, 6, 0, 0);
        CodeToolbar.Visibility = Visibility.Visible; CodeToolbarRow.Height = GridLength.Auto;
        ComposerToolsScroll.MaxHeight = compact ? 0 : 90;
        CompactActions.Visibility = !_showTeacherFiles && compact && _selected?.IsTeacher == true ? Visibility.Visible : Visibility.Collapsed;
        FilterAllButton.Background = InboxFilter.SelectedIndex == 0 ? new SolidColorBrush(Color.FromRgb(237,242,255)) : Brushes.White;
        FilterUnreadButton.Background = InboxFilter.SelectedIndex == 1 ? new SolidColorBrush(Color.FromRgb(237,242,255)) : Brushes.White;
    }
    private void BackToInboxClick(object sender, RoutedEventArgs e) { _showInbox = true; _userOpened = false; UpdateShellLayout(); ViewChanged(); }
    private void CompactActionsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CompactActions };
        foreach (var reaction in ClassroomReactions.All.Where(r => r.TeacherOnly == (_selected?.IsTeacher == true)))
        { var item = new MenuItem { Header = reaction.Text, Tag = reaction.Id, IsEnabled = CanSendReaction && _selected?.CanSend == true }; item.Click += ReactionSendClick; menu.Items.Add(item); }
        menu.IsOpen = true;
    }
    internal void ShowConversationForLayoutCheck() { _showInbox = false; UpdateShellLayout(); }
    internal void ShowInboxForLayoutCheck() { _showInbox = true; UpdateShellLayout(); }
    internal void ShowSettingsForLayoutCheck(bool show) { SettingsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed; ViewChanged(); }
    private async void OpenSettingsClick(object sender, RoutedEventArgs e) { ContactSettingsPanel.Visibility = _runtime?.ArchivedIdentityFixture == true ? Visibility.Visible : Visibility.Collapsed; SettingsPane.Visibility = Visibility.Visible; ViewChanged(); await LoadManualFormAsync(_selected?.RosterId); }
    private void CloseSettingsClick(object sender, RoutedEventArgs e) { ++_manualFormGeneration; SettingsPane.Visibility = Visibility.Collapsed; ViewChanged(); }
    private async void AddRosterClick(object sender, RoutedEventArgs e) => await AddContactDialogAsync(false);
    private async void AddTeacherClick(object sender, RoutedEventArgs e) => await AddContactDialogAsync(true);
    private async void TeacherSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_changingSelection && TeachersList.SelectedItem is InboxRowView row) await SelectEntryAsync(row.Entry, true); }
    private async Task LoadManualFormAsync(string? rosterId)
    {
        SaveContactFeedback.Text = ""; _editingRosterId = rosterId; _manualFormSnapshot = null; _pairingSelection = null; RefreshPairingControls(); long generation = ++_manualFormGeneration; SaveContactButton.IsEnabled = DeleteContactButton.IsEnabled = false;
        ManualRosterTitle.Text = rosterId is null ? "加入好友" : "編輯好友 IP";
        SaveContactButton.Content = rosterId is null ? "加入好友" : "儲存";
        if (_runtime is null) { ContactName.Text = StudentNumberBox.Text = RecipientIp.Text = ""; TeacherLabel.IsChecked = false; return; }
        try
        {
            var loaded = await _runtime.Worker.Run(s => (Rows: s.GetRosterSnapshot(), Target: rosterId is null ? null : s.CapturePairingRosterTarget(rosterId)));
            var rows = loaded.Rows;
            if (generation != _manualFormGeneration) return;
            var row = rosterId is null ? null : rows.FirstOrDefault(r => r.Entry.Id == rosterId);
            if (rosterId is not null && row is null) { SetStorageNotice("這筆名單已移除，請重新開啟設定。"); return; }
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
                await SyncTeacherProjectionAsync();
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
                SetStorageNotice(RosterFailure(result.Reason));
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
            if (!preview.CanDelete) { SetStorageNotice(RosterFailure(preview.Reason)); return; }
            if (ContactPrompt("只從這台電腦的名單移除，歷史會保留在封存區，不會封鎖對方。", "移除聯絡人", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await FlushDraftsAsync();
            await _runtime.InvalidatePairingAsync("roster_delete");
            var result = await _runtime.Worker.Run(s => s.DeleteRosterEntry(id, preview.SnapshotToken, preview.ToConfirmation()));
            if (!result.Changed) { SetStorageNotice(RosterFailure(result.Reason)); return; }
            await SyncTeacherProjectionAsync();await LoadManualFormAsync(null); _runtime.Presence?.Refresh(); ResetRecipientProtocolHints(); await CompleteContactRemovalAsync(id);
        }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private static string RosterFailure(string reason) => reason switch {
        "roster_changed" => "名單已變動，尚未儲存。請保留目前輸入，重新開啟設定後再檢查。",
        "confirmation_mismatch" => "名單已被修改，請重新開啟設定後再確認刪除。",
        "not-found" => "這筆名單已不存在。",
        "duplicate-endpoint" => "這個 IP 已在名單中，可開啟既有對話。",
        "blocked" => "此 IP 已封鎖，請先解除封鎖。",
        "merge_conflict" => "兩邊的草稿或訊息有衝突，資料已保留，請先處理再合併。",
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
    private async void AttachFileClick(object sender, RoutedEventArgs e)
    {
        if (!Draft.IsEnabled) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選取要傳送的相片或檔案",
            Filter = "所有支援檔案|*.*|圖片檔案 (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|文件檔案 (*.pdf;*.txt)|*.pdf;*.txt"
        };
        if (picker.ShowDialog(this) == true)
        {
            await AddAttachmentPathsAsync(new[] { picker.FileName });
        }
    }
    private void ComposerDragOver(object sender, DragEventArgs e)
    {
        bool files;
        try { files = e.Data.GetDataPresent(DataFormats.FileDrop); }
        catch (System.Runtime.InteropServices.COMException) { files = false; }
        bool destinationReady = _showTeacherFiles ? CanDropClassroomFile() : CanAddAttachment() && _attachmentDraft is null;
        e.Effects = files && destinationReady && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects != DragDropEffects.Copy) return;
        if (_showTeacherFiles)
        {
            TeacherFilesPane.Background = new SolidColorBrush(Color.FromRgb(243, 246, 250));
            ClassroomHostState.Text = "放開即分享給全班 · 一次一個檔案";
        }
        else if (ComposerBorder is not null)
        {
            ComposerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 89, 197));
            ComposerBorder.Background = new SolidColorBrush(Color.FromRgb(243, 246, 250));
        }
    }
    private void ResetFileDropHighlight()
    {
        if (ComposerBorder is not null)
        {
            ComposerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 227, 236));
            ComposerBorder.Background = Brushes.Transparent;
        }
        TeacherFilesPane.Background = new SolidColorBrush(Color.FromRgb(252, 253, 255));
        if (_showTeacherFiles && _runtime?.ClassroomFiles is { } shares) ClassroomHostState.Text = shares.HostStatus;
    }
    private void ComposerDragLeave(object sender, DragEventArgs e) => ResetFileDropHighlight();
    private async void ComposerDrop(object sender, DragEventArgs e)
    {
        ResetFileDropHighlight();
        // Both destinations own file drops, including rejected drops: never let TextBox insert a filename.
        e.Handled = true;
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            if (_showTeacherFiles) await AddClassroomUploadPathsAsync(files);
            else await AddAttachmentPathsAsync(files);
        }
        catch (Exception error) { SetStatus(AttachmentException.Describe(error)); }
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
        try { long edit = ++_readPolicyEdit; var policy = await _runtime.Worker.Run(s => s.GetReadReceiptPolicy()); if (edit == _readPolicyEdit) { ReadReceiptToggle.IsChecked = policy.Enabled; ReadReceiptToggle.IsEnabled = true; } await SyncTeacherProjectionAsync(); await FlushDraftsAsync(); _historyLoaded = false; await RefreshInboxAsync(true); }
        catch (Exception ex) { ShowStorageError(ex); }
    }
    private void ShowStorageError(Exception exception, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_closingRequested && exception is OperationCanceledException) return;
        var stage = caller == nameof(MarkViewportAsync) ? DiagnosticStage.UiRead : caller is nameof(RefreshInboxAsync) or nameof(LoadHistoryAsync) ? DiagnosticStage.UiRefresh : caller is nameof(SendCoreAsync) or nameof(SendBoundaryAsync) or nameof(SendClassroomCoreAsync) or nameof(QueueCurrentMessageAsync) ? DiagnosticStage.SendPrepare : caller == nameof(FlushDraftsAsync) ? DiagnosticStage.StorageWrite : DiagnosticStage.UiAction;
        var fault = DiagnosticFault.FromException(exception, exception is OperationCanceledException && _closingRequested);
        _runtime?.Diagnostics.Record(stage, DiagnosticOutcome.Failed, error: exception);
        _diagnosticFailures = DiagnosticRecoveryPolicy.Report(_diagnosticFailures, stage, fault);
        RefreshDiagnosticStatus();
    }
    private static string DiagnosticMessage(DiagnosticFault fault) => (fault.Code switch {
        DiagnosticCode.StorageBusy or DiagnosticCode.QueueFull => "本機資料忙碌，這次操作未完成，請稍後重試。",
        DiagnosticCode.DiskFull => "磁碟空間不足，請釋放空間後重試；請保留目前草稿。",
        DiagnosticCode.ReadOnly => "本機資料無法寫入，請檢查資料夾權限後重試。",
        DiagnosticCode.CorruptData or DiagnosticCode.UnsupportedData => "本機資料需要檢查，請保留原檔並匯出診斷；不要刪除資料庫。",
        DiagnosticCode.NetworkUnavailable or DiagnosticCode.Timeout => "連線未完成，請確認對方已開啟程式；已入列訊息會依待送狀態續送。",
        DiagnosticCode.InvalidPayload => "資料格式不正確，請檢查輸入後重試。",
        _ => "這次操作未完成，請重試；若持續發生，請從設定匯出診斷。"
    }) + "（" + fault.DisplayCode + "）";
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
    internal bool IsSelectedReactionTarget(string target, ClassroomReaction reaction) =>
        _selected is { } entry && entry.Endpoint == target && ClassroomReactionPolicy.CanSend(entry.IsTeacher, entry.Removed, entry.CanSend, reaction.TeacherOnly);
    internal bool CanSendReaction => !_closingRequested && _receiverRunning && !_sending;
    internal void AbortClosing() { IsEnabled = true; _closingRequested = false; _stopping = false; UpdateComposer(); }
}
