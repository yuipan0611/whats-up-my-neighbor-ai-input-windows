using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NekoChat.Core.Attachments;
using NekoChat.Core.Presentation;
using NekoChat.Core.Storage;
using NekoChat.Core.Classroom.Transport;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private bool _showTeacherFiles;
    private long _teacherFilesRequest;
    private long? _teacherFilesBefore;
    private readonly ObservableCollection<TeacherFileView> _teacherFiles = [];

    private void InitializeUi4() => TeacherFilesList.ItemsSource = _sharedFiles;
    private void FilterAllClick(object sender, RoutedEventArgs e) => InboxFilter.SelectedIndex = 0;
    private void FilterUnreadClick(object sender, RoutedEventArgs e) => InboxFilter.SelectedIndex = 1;

    private async void TeacherFilesClick(object sender, RoutedEventArgs e)
    {
        await FlushDraftsAsync();
        if (_closingRequested) return;
        _showTeacherFiles = true; _showInbox = false; _userOpened = false;
        History.Visibility = Visibility.Collapsed; ApplyInboxFilter();
        UpdateHeader(); UpdateComposer(); UpdateShellLayout(); ViewChanged();
        await LoadTeacherFilesAsync(false);
    }
    private async void MoreTeacherFilesClick(object sender, RoutedEventArgs e) => await LoadTeacherFilesAsync(true);
    private async Task LoadTeacherFilesAsync(bool older)
    {
        if (_runtime?.AttachmentWarning is { } warning)
        {
            UploadClassroomFileButton.IsEnabled = false;
            MoreTeacherFilesButton.Visibility = Visibility.Collapsed;
            TeacherFilesState.Text = warning; TeacherFilesState.Visibility = Visibility.Visible;
            ClassroomHostState.Text = "附件功能暫停 · 文字聊天仍可用";
            return;
        }
        if (_runtime?.ClassroomFiles is not null) { await LoadSharedFilesAsync(older); return; }
        if (!_showTeacherFiles || _runtime is not { } runtime || runtime.ArchivedIdentityFixture) return;
        long generation = ++_teacherFilesRequest; long? before = older ? _teacherFilesBefore : null;
        MoreTeacherFilesButton.IsEnabled = false;
        TeacherFilesState.Text = "載入老師檔案中…";
        if (_teacherFiles.Count == 0) TeacherFilesState.Visibility = Visibility.Visible;
        try
        {
            var page = await runtime.Worker.Run(s => s.ListTeacherFiles(before));
            if (!_showTeacherFiles || _stopping || generation != _teacherFilesRequest || !ReferenceEquals(runtime, _runtime)) return;
            if (!older) _teacherFiles.Clear();
            foreach (var item in page.Items)
            {
                var row = item.Attachment;
                string? path = runtime.Attachments is { } service && service.Vault.Exists(row.File) ? service.Vault.PathFor(row.File.BlobId) : null;
                if (_teacherFiles.All(x => x.Entry.Attachment.LocalSeq != row.LocalSeq))
                    _teacherFiles.Add(new(item, path, AttachmentActionAsync, () => OpenTeacherFileMessageAsync(item)));
            }
            _teacherFilesBefore = page.NextBeforeLocalSeq;
            MoreTeacherFilesButton.Visibility = _teacherFilesBefore is null ? Visibility.Collapsed : Visibility.Visible;
            TeacherFilesState.Text = "還沒有老師檔案。\n老師分享的附件會整理在這裡。";
            TeacherFilesState.Visibility = _teacherFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (generation != _teacherFilesRequest) return;
            TeacherFilesState.Text = "無法讀取老師檔案，請重新讀取。";
            TeacherFilesState.Visibility = Visibility.Visible; ShowStorageError(ex);
        }
        finally { if (generation == _teacherFilesRequest) MoreTeacherFilesButton.IsEnabled = true; }
    }
    private async Task OpenTeacherFileMessageAsync(TeacherFileEntry file)
    {
        if (_runtime is not { } runtime) return;
        long request = ++_teacherFilesRequest;
        try
        {
            var loaded = await runtime.Worker.Run(s => (Snapshot: s.GetClassroomInboxSnapshot().ToInboxSnapshot(),
                File: s.ListTeacherFiles(checked(file.Attachment.LocalSeq + 1), 1).Items.FirstOrDefault(x => x.Attachment.LocalSeq == file.Attachment.LocalSeq)));
            if (!_showTeacherFiles || request != _teacherFilesRequest || !ReferenceEquals(runtime, _runtime)) return;
            var entry = loaded.Snapshot.Entries.FirstOrDefault(x => x.IsTeacher && !x.Removed && x.ConversationId == file.Attachment.Principal);
            if (loaded.File is null || entry is null) { SetStatus("原訊息已清除或老師名單已變更。" ); await LoadTeacherFilesAsync(false); return; }
            _snapshot = loaded.Snapshot;
            await SelectEntryAsync(entry, true, file.Attachment.LocalSeq);
        }
        catch (Exception ex) { if (request == _teacherFilesRequest) ShowStorageError(ex); }
    }

    private void ConversationMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ConversationMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var rename = new MenuItem { Header = "編輯綽號…", IsEnabled = _selected?.RosterId is not null };
        if (_selected is { } entry) rename.CommandParameter = new InboxRowView(entry, StateOf(entry));
        rename.Click += RenameNicknameClick; menu.Items.Add(rename);
        var details = new MenuItem { Header = "編輯對象 IP…", IsEnabled = _selected?.RosterId is not null };
        details.Click += async (_, _) => { SettingsPane.Visibility = Visibility.Visible; ContactSettingsPanel.Visibility = Visibility.Visible; ViewChanged(); await LoadManualFormAsync(_selected?.RosterId); };
        menu.Items.Add(details); menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清除此對話…", IsEnabled = ClearConversationButton.IsEnabled };
        clear.Click += ClearConversationClick; menu.Items.Add(clear); menu.IsOpen = true;
    }
    private void StickerClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = StickerButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var sticker = new MenuItem { Header = "你再給我滑！", Tag = "stop-scrolling", IsEnabled = ScrollReaction.IsEnabled };
        sticker.Click += ReactionSendClick; menu.Items.Add(sticker); menu.IsOpen = true;
    }
    private void OpenCodeEditorClick(object sender, RoutedEventArgs e)
    {
        if (!Draft.IsEnabled || _showTeacherFiles) return;
        string conversation = _selected?.ConversationId ?? "";
        var panel = new StackPanel { Margin = new(20) };
        var languages = new ComboBox { Margin = new(0,4,0,12) };
        foreach (var source in CodeLanguage.Items.OfType<ComboBoxItem>()) languages.Items.Add(new ComboBoxItem { Content = source.Content, Tag = source.Tag });
        languages.SelectedIndex = CodeLanguage.SelectedIndex;
        var input = new TextBox { AcceptsReturn = true, AcceptsTab = true, Text = Draft.SelectedText, TextWrapping = TextWrapping.NoWrap, Height = 180,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), Margin = new(0,4,0,12) };
        System.Windows.Automation.AutomationProperties.SetName(input, "程式碼");
        panel.Children.Add(new Label { Content = "語言", Target = languages, Padding = new(0) }); panel.Children.Add(languages);
        panel.Children.Add(new Label { Content = "程式碼", Target = input, Padding = new(0) }); panel.Children.Add(input);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0,0,8,0) };
        var insert = new Button { Content = "插入訊息" }; buttons.Children.Add(cancel); buttons.Children.Add(insert); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = "插入程式碼", Width = Math.Min(500, SystemParameters.WorkArea.Width - 32), SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        insert.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => input.Focus();
        if (dialog.ShowDialog() != true || !Draft.IsEnabled || conversation != (_selected?.ConversationId ?? "")) return;
        CodeLanguage.SelectedIndex = languages.SelectedIndex;
        string language = (languages.SelectedItem as ComboBoxItem)?.Tag as string ?? "text";
        Draft.SelectedText = "\n```" + language + "\n" + input.Text + "\n```\n"; Draft.Focus();
    }

    private void SyncAttemptMessage()
    {
        if (_runtime?.ArchivedIdentityFixture != false)
        {
            foreach (var old in History.Rows.Where(x => x.AttemptId is not null && x.AttemptId != _selected?.Attempt?.MessageId).ToArray()) History.Rows.Remove(old);
            SyncAttemptRow(_selected?.Attempt, "", 0);
            return;
        }
        if (_selected is null || _showTeacherFiles) return;
        var pendingMessages = _pendingConversation == _selected.ConversationId ? _pendingMessages : [];
        var ids = pendingMessages.Select(p => p.Attempt.MessageId).ToHashSet(StringComparer.Ordinal);
        if (_selected.Attempt is { } latest) ids.Add(latest.MessageId);
        foreach (var old in History.Rows.Where(x => x.AttemptId is not null && !ids.Contains(x.AttemptId)).ToArray()) History.Rows.Remove(old);
        foreach (var pending in pendingMessages)
        {
            var a = pending.Attempt;
            var summary = _selected.Attempt?.MessageId == a.MessageId ? _selected.Attempt :
                new PendingSendSummary(a.MessageId, a.State, a.RawSnapshot, a.TryCount, a.DraftEpoch, a.DraftRevision);
            SyncAttemptRow(summary, pending.CreatedUtc, pending.Sequence);
        }
        if (_selected.Attempt is { } active && pendingMessages.All(p => p.Attempt.MessageId != active.MessageId)) SyncAttemptRow(active, "", 0);
    }

    private void SyncAttemptRow(PendingSendSummary? attempt, string utc, long sequence)
    {
        if (attempt is null || _selected is null || _showTeacherFiles) return;
        bool cancelling = _runtime?.IsAttemptCancellationRequested(attempt.MessageId, attempt.TryCount) == true;
        var entry = _selected;
        var row = History.Rows.FirstOrDefault(x => x.AttemptId == attempt.MessageId);
        bool added = row is null;
        row ??= new MessageView("attempt:" + attempt.MessageId, attempt.RawSnapshot, true, false, sequence, utc, "") { AttemptId = attempt.MessageId };
        row.AttemptTryCount = attempt.TryCount;
        row.AttemptFailed = attempt.State is "failed" or "unconfirmed";
        bool retry = !_closingRequested && !_sending && !_attachmentDraftLoading && _attachmentDraft is null &&
            (_runtime?.ArchivedIdentityFixture == true ? entry.Pairing == PairingState.Paired : entry.CanSend) && _runtime?.CanSend == true && attempt.State != "sending" &&
            (Draft.Text.Length == 0 || Draft.Text == attempt.RawSnapshot);
        row.RetrySend = retry ? async () => {
            if (_runtime?.ArchivedIdentityFixture == false && ClassroomPrincipalKey.TryParse(entry.ConversationId, out var principal))
            {
                var fresh = await _runtime.Worker.Run(s => s.ListClassroomPendingMessages(principal, 200, sequence > 0 ? checked(sequence + 1) : null).Any(p => p.Attempt.MessageId == attempt.MessageId && p.Attempt.TryCount == attempt.TryCount && p.Attempt.State != "sending"));
                if (!fresh) return;
            }
            if (_selected?.Id != entry.Id || _sending || !_activeSend.IsCompleted || _attachmentDraftLoading || _attachmentDraft is not null ||
                Draft.Text.Length > 0 && Draft.Text != attempt.RawSnapshot) return;
            if (Draft.Text.Length == 0) Draft.Text = attempt.RawSnapshot;
            await SendCurrentAsync(true, attempt);
        } : null;
        row.CancelSend = _selected.Attempt?.MessageId == attempt.MessageId && CancelAttemptButton.IsEnabled ?
            () => { if (_selected is { Attempt: { } current } selected && selected.Id == entry.Id && current.MessageId == attempt.MessageId && current.TryCount == attempt.TryCount) CancelAttemptClick(this, new RoutedEventArgs()); return Task.CompletedTask; } : null;
        row.DeleteLocal = _runtime?.ArchivedIdentityFixture == false && !_sending && attempt.State != "sending" ?
            () => ConfirmDeleteLocalMessageAsync(entry, row) : null;
        row.Delivery = ClassroomSendUiPolicy.DecideAttemptBanner(attempt.State, cancelling);
        row.RefreshActions();
        if (added) History.Rows.Add(row);
        HistoryState.Visibility = Visibility.Collapsed;
    }
}
